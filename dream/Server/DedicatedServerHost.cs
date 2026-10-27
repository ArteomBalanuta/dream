using System.Collections.Concurrent;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Server;

public sealed class DedicatedServerHost : IDisposable, IServerSessionClaimRegistry
{
    private static readonly PlayerSimulationTuning DefaultServerTuning = PlayerSimulationTuning.AuthoritativeDefault;

    // ConcurrentDictionary enumeration order follows string hash codes, which .NET randomizes per
    // process, so joined sessions are put in ordinal session-id order before they drive the tick.
    private static readonly Comparison<PlayerSessionId> CompareSessionIdsOrdinal =
        static (left, right) => string.CompareOrdinal(left.Value, right.Value);

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
    {
        _authority = authority;
        _tickDrivenAuthority = authority as ITickDrivenAuthoritativeSessionHost;
        _router = router;
        _checkpointWrites = Runtime?.CheckpointWriter is { } checkpointWriter
            ? new BackgroundCheckpointWriter(checkpointWriter)
            : null;
    }

    public BackgroundCheckpointWriter? CheckpointWrites => _checkpointWrites;

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

    public static DedicatedServerHost CreateRuntimeAttached(Dream.Server.Persistence.ServerPersistenceBundle? persistence = null)
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

        return new DedicatedServerHost(RuntimeAuthoritativeSessionHost.CreateDefault(persistence), router);
    }

    public IReadOnlyList<IServerToClientMessage> HandleClientMessage(
        string connectionId,
        IClientToServerMessage message)
    {
        ServerRoutingContext context = GetOrCreateConnectionContext(connectionId);
        lock (_authoritySync)
        {
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
