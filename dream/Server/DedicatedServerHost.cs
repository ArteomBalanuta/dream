using System.Collections.Concurrent;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Server;

// A reply produced for a connection outside HandleClientMessage, such as a deferred join's
// JoinAccepted or JoinRejected.
public readonly record struct PendingConnectionReply(string ConnectionId, IServerToClientMessage Message);

public sealed class DedicatedServerHost : IDisposable, IServerSessionClaimRegistry
{
    private static readonly PlayerSimulationTuning DefaultServerTuning = PlayerSimulationTuning.AuthoritativeDefault;

    // ConcurrentDictionary enumeration order follows string hash codes, which .NET randomizes per
    // process, so joined sessions are put in ordinal session-id order before they drive the tick.
    private static readonly Comparison<PlayerSessionId> CompareSessionIdsOrdinal =
        static (left, right) => string.CompareOrdinal(left.Value, right.Value);

    // Joins applied at a tick boundary go in ordinal requested-session-id order, then connection id.
    private static readonly Comparison<PendingJoin> ComparePendingJoinsOrdinal =
        static (left, right) =>
        {
            int bySession = string.CompareOrdinal(left.Request.RequestedSessionId, right.Request.RequestedSessionId);
            return bySession != 0 ? bySession : string.CompareOrdinal(left.ConnectionId, right.ConnectionId);
        };

    private readonly IProtocolRouter _router;
    private readonly IAuthoritativeSessionHost? _authority;
    private readonly ITickDrivenAuthoritativeSessionHost? _tickDrivenAuthority;
    private readonly LatestPlayerCommandInbox _commandInbox = new();
    private readonly PlayerActionQueue _actionQueue = new();
    private readonly ServerTickRunner _tickRunner = new();
    private readonly ConcurrentDictionary<string, ServerRoutingContext> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionClaims = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _connectionClaims = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<AuthoritativeActionResult> _pendingActionResults = new();
    private readonly ConcurrentQueue<PendingConnectionReply> _pendingJoinReplies = new();
    private readonly bool _deferJoinsToTick;
    // Join requests received since the last tick, touched only under _authoritySync. The tick
    // swaps in _applyingJoins before routing, because a failed join releases its connection and
    // ReleaseConnection edits _pendingJoins.
    private List<PendingJoin> _pendingJoins = [];
    private List<PendingJoin> _applyingJoins = [];
    private readonly object _authoritySync = new();

    // Tick scratch, touched only under _authoritySync. The context list mirrors _connections and
    // is rebuilt only after a connection is added or removed.
    private readonly List<ServerRoutingContext> _connectionContexts = [];
    private readonly List<PlayerSessionId> _joinedSessionIds = [];
    private readonly List<PlayerCommandFrame> _tickCommands = [];
    private readonly List<QueuedPlayerAction> _tickActions = [];
    private int _connectionsChanged = 1;
    private readonly BackgroundCheckpointWriter? _checkpointWrites;
    private double _checkpointElapsedSeconds;

    public DedicatedServerHost(
        IAuthoritativeSessionHost? authority,
        IProtocolRouter router)
        : this(authority, router, deferJoinsToTick: false)
    {
    }

    // With deferJoinsToTick, a join request is not applied when it arrives: it waits for the next
    // tick and every join that arrived since the previous tick is applied at the start of that tick
    // in ordinal session-id order. The runtime's join-order state (primary seat, secondary actor
    // ids and spawn lanes) then depends only on which sessions joined in which tick, not on their
    // arrival order. HandleClientMessage returns no reply for a deferred join; the JoinAccepted or
    // JoinRejected comes from DrainPendingJoinRepliesInto after the tick, and the caller must
    // deliver it. DedicatedServerService delivers both kinds of reply; the dedicated and
    // integrated servers turn this on through ServerBootstrap.CreateAuthoritativeHost.
    public DedicatedServerHost(
        IAuthoritativeSessionHost? authority,
        IProtocolRouter router,
        bool deferJoinsToTick)
    {
        _authority = authority;
        _deferJoinsToTick = deferJoinsToTick;
        _tickDrivenAuthority = authority as ITickDrivenAuthoritativeSessionHost;
        _router = router;
        _checkpointWrites = Runtime?.CheckpointWriter is { } checkpointWriter
            ? new BackgroundCheckpointWriter(checkpointWriter)
            : null;
    }

    public BackgroundCheckpointWriter? CheckpointWrites => _checkpointWrites;

    public bool DefersJoinsToTick => _deferJoinsToTick;

    // How long until the tick runner's next fixed tick is due.
    public double SecondsUntilNextTick =>
        TickPacer.SecondsUntilNextTick(_tickRunner.RemainingSeconds, SimulationClock.FixedDeltaSeconds);

    public WorldSessionRuntime? Runtime => (_authority as RuntimeAuthoritativeSessionHost)?.Runtime;

    public static DedicatedServerHost CreateDefault()
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler()
            ],
            Array.Empty<IClientMessageHandler>());

        return new DedicatedServerHost(authority: null, router);
    }

    public static DedicatedServerHost CreateRuntimeAttached(
        Dream.Server.Persistence.ServerPersistenceBundle? persistence = null,
        bool deferJoinsToTick = false)
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler()
            ],
            Array.Empty<IClientMessageHandler>());

        return new DedicatedServerHost(RuntimeAuthoritativeSessionHost.CreateDefault(persistence), router, deferJoinsToTick);
    }

    public IReadOnlyList<IServerToClientMessage> HandleClientMessage(
        string connectionId,
        IClientToServerMessage message)
    {
        ServerRoutingContext context = GetOrCreateConnectionContext(connectionId);
        lock (_authoritySync)
        {
            if (_deferJoinsToTick && message is ClientJoinRequestMessage joinRequest)
            {
                _pendingJoins.Add(new PendingJoin(connectionId, joinRequest));
                return Array.Empty<IServerToClientMessage>();
            }

            context.ClearOutboundMessages();
            _router.RouteServerInbound(context, message);
            return context.OutboundMessages.ToArray();
        }
    }

    public bool TryGetConnectionContext(string connectionId, out ServerRoutingContext? context)
    {
        return _connections.TryGetValue(connectionId, out context);
    }

    public bool IsConnectionJoined(string connectionId)
    {
        return _connections.TryGetValue(connectionId, out ServerRoutingContext? context)
            && context.LastJoinRequest != null;
    }

    public int AdvanceTime(double elapsedSeconds)
    {
        if (_tickDrivenAuthority == null)
        {
            throw new InvalidOperationException(
                "Cannot advance server time before a runtime-backed authoritative session host is attached.");
        }

        lock (_authoritySync)
        {
            return _tickRunner.Advance(
                elapsedSeconds,
                this,
                static (host, tick, fixedDeltaSeconds) => host.RunTick(tick, fixedDeltaSeconds));
        }
    }

    public IReadOnlyList<AuthoritativeActionResult> DrainPendingActionResults()
    {
        List<AuthoritativeActionResult> drained = [];
        DrainPendingActionResultsInto(drained);
        return drained;
    }

    // Replaces the destination's contents with every pending action result, oldest first.
    public void DrainPendingActionResultsInto(List<AuthoritativeActionResult> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        destination.Clear();
        while (_pendingActionResults.TryDequeue(out AuthoritativeActionResult actionResult))
        {
            destination.Add(actionResult);
        }
    }

    // Replaces the destination's contents with the replies to joins applied at tick boundaries,
    // in the order the joins were applied. Empty unless the host defers joins to the tick.
    public void DrainPendingJoinRepliesInto(List<PendingConnectionReply> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        destination.Clear();
        while (_pendingJoinReplies.TryDequeue(out PendingConnectionReply reply))
        {
            destination.Add(reply);
        }
    }

    public ServerSnapshotMessage CreateSnapshotMessage(bool includeWorldSnapshot = false)
    {
        if (_authority == null)
        {
            throw new InvalidOperationException(
                "Cannot export a server snapshot before an authoritative session host is attached.");
        }

        lock (_authoritySync)
        {
            return new ServerSnapshotMessage(_authority.ExportSnapshot(includeWorldSnapshot));
        }
    }

    // With no joined receiver, pending world and damage deltas have nobody to reach. Later joiners
    // get the current world in JoinAccepted, so the backlog is dropped instead of replayed on top of it.
    public void DiscardUndeliveredSnapshotDeltas()
    {
        if (_authority == null)
        {
            return;
        }

        lock (_authoritySync)
        {
            _ = _authority.ExportSnapshot();
        }
    }

    public bool TryClaimSession(string connectionId, string sessionId, out string? rejectionReason)
    {
        if (_connectionClaims.TryGetValue(connectionId, out string? existingSession))
        {
            if (string.Equals(existingSession, sessionId, StringComparison.Ordinal))
            {
                rejectionReason = null;
                return true;
            }

            rejectionReason = $"Connection is already joined as session '{existingSession}'.";
            return false;
        }

        if (_sessionClaims.TryAdd(sessionId, connectionId))
        {
            _connectionClaims[connectionId] = sessionId;
            rejectionReason = null;
            return true;
        }

        if (_sessionClaims.TryGetValue(sessionId, out string? ownerConnection)
            && string.Equals(ownerConnection, connectionId, StringComparison.Ordinal))
        {
            _connectionClaims[connectionId] = sessionId;
            rejectionReason = null;
            return true;
        }

        rejectionReason = $"Session '{sessionId}' is already joined by another connection.";
        return false;
    }

    public void ReleaseConnection(string connectionId)
    {
        if (_deferJoinsToTick)
        {
            lock (_authoritySync)
            {
                _pendingJoins.RemoveAll(join => string.Equals(join.ConnectionId, connectionId, StringComparison.Ordinal));
            }
        }

        if (_connectionClaims.TryRemove(connectionId, out string? sessionId))
        {
            if (_sessionClaims.TryGetValue(sessionId, out string? ownerConnection)
                && string.Equals(ownerConnection, connectionId, StringComparison.Ordinal))
            {
                _sessionClaims.TryRemove(sessionId, out _);
            }

            if (_authority is RuntimeAuthoritativeSessionHost runtimeAuthority)
            {
                lock (_authoritySync)
                {
                    runtimeAuthority.ReleaseSession(sessionId);
                }
            }
        }

        if (_connections.TryRemove(connectionId, out _))
        {
            Volatile.Write(ref _connectionsChanged, 1);
        }
    }

    public void Dispose()
    {
        // Finish pending checkpoint writes before the authority disposes its persistence.
        _checkpointWrites?.Dispose();
        switch (_authority)
        {
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private ServerRoutingContext GetOrCreateConnectionContext(string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out ServerRoutingContext? existing))
        {
            return existing;
        }

        ServerRoutingContext context = _connections.GetOrAdd(
            connectionId,
            static (id, state) => new ServerRoutingContext(
                id,
                state.Authority,
                state.CommandInbox,
                state.ActionQueue,
                state.SessionClaims),
            (Authority: _authority,
                CommandInbox: (IPlayerCommandInbox)_commandInbox,
                ActionQueue: (IPlayerActionQueue)_actionQueue,
                SessionClaims: (IServerSessionClaimRegistry)this));
        Volatile.Write(ref _connectionsChanged, 1);
        return context;
    }

    private void RunTick(ulong tick, float fixedDeltaSeconds)
    {
        ApplyPendingJoins();
        RefreshJoinedSessionIds();
        _commandInbox.DrainInto(_joinedSessionIds, _tickCommands);
        _actionQueue.DrainInto(_joinedSessionIds, _tickActions);
        IReadOnlyList<AuthoritativeActionResult> actionResults = _tickDrivenAuthority!.AdvanceTick(
            tick,
            fixedDeltaSeconds,
            _tickCommands,
            _tickActions,
            DefaultServerTuning,
            allowGameplaySimulation: true);
        for (int i = 0; i < actionResults.Count; i++)
        {
            _pendingActionResults.Enqueue(actionResults[i]);
        }

        WorldSessionRuntime? runtime = Runtime;
        if (runtime != null)
        {
            _checkpointElapsedSeconds += fixedDeltaSeconds;
            while (_checkpointElapsedSeconds >= 5d)
            {
                // Capture is in-memory; the database write runs off the tick thread.
                if (_checkpointWrites != null &&
                    runtime.CapturePersistenceCheckpoint() is { } checkpoint)
                {
                    _checkpointWrites.Enqueue(checkpoint);
                }

                _checkpointElapsedSeconds -= 5d;
            }
        }
    }

    // Same-tick joins in canonical order. A join whose connection was dropped is skipped. With no
    // pending join this returns before touching anything, so steady-state ticks do not allocate.
    private void ApplyPendingJoins()
    {
        if (_pendingJoins.Count == 0)
        {
            return;
        }

        List<PendingJoin> joins = _pendingJoins;
        _pendingJoins = _applyingJoins;
        _applyingJoins = joins;
        if (joins.Count > 1)
        {
            joins.Sort(ComparePendingJoinsOrdinal);
        }

        for (int i = 0; i < joins.Count; i++)
        {
            PendingJoin join = joins[i];
            if (!_connections.TryGetValue(join.ConnectionId, out ServerRoutingContext? context))
            {
                continue;
            }

            context.ClearOutboundMessages();
            _router.RouteServerInbound(context, join.Request);
            for (int replyIndex = 0; replyIndex < context.OutboundMessages.Count; replyIndex++)
            {
                _pendingJoinReplies.Enqueue(new PendingConnectionReply(join.ConnectionId, context.OutboundMessages[replyIndex]));
            }

            context.ClearOutboundMessages();
        }

        joins.Clear();
    }

    private readonly record struct PendingJoin(string ConnectionId, ClientJoinRequestMessage Request);

    // Connections are added and removed off the tick thread. Enumerating the ConcurrentDictionary
    // is safe during those changes, and a change made mid-enumeration sets the flag again for the
    // next tick. Join state is re-read every tick because LastJoinRequest changes on its own, and
    // the result is sorted in place (ordinal), so the command drain, the runtime tick and the
    // synthesized primary-use actions see sessions in the same order on every server process.
    private void RefreshJoinedSessionIds()
    {
        if (Interlocked.Exchange(ref _connectionsChanged, 0) != 0)
        {
            _connectionContexts.Clear();
            foreach (KeyValuePair<string, ServerRoutingContext> connection in _connections)
            {
                _connectionContexts.Add(connection.Value);
            }
        }

        _joinedSessionIds.Clear();
        for (int i = 0; i < _connectionContexts.Count; i++)
        {
            if (_connectionContexts[i].LastJoinRequest is { } join)
            {
                _joinedSessionIds.Add(new PlayerSessionId(join.RequestedSessionId));
            }
        }

        if (_joinedSessionIds.Count > 1)
        {
            _joinedSessionIds.Sort(CompareSessionIdsOrdinal);
        }
    }
}
