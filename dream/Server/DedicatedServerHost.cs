using System.Collections.Concurrent;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Server;

// A reply produced for a connection outside HandleClientMessage, such as a deferred join's
// JoinAccepted or JoinRejected, or a world resync served after a tick.
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

    // How many ticks a deferred join may wait for its player's persisted state to load (30 s). The
    // load normally finishes within a few ticks; this only bounds a database that stays locked.
    // A join past the bound, or whose load failed twice, is rejected with JoinRetryRejectionReason
    // instead of reading the database on the tick.
    internal const int MaxJoinPrefetchDeferralTicks = 30 * SimulationClock.AuthoritativeTicksPerSecond;

    public const string JoinRetryRejectionReason =
        "The server could not load this player's saved data in time; retry the join.";

    private readonly IProtocolRouter _router;
    private readonly IAuthoritativeSessionHost? _authority;
    private readonly ITickDrivenAuthoritativeSessionHost? _tickDrivenAuthority;
    private readonly BufferedPlayerCommandInbox _commandInbox = new();
    private readonly PlayerActionQueue _actionQueue = new();
    private readonly ServerTickRunner _tickRunner = new();
    private readonly ConcurrentDictionary<string, ServerRoutingContext> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionClaims = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _connectionClaims = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<AuthoritativeActionResult> _pendingActionResults = new();
    private readonly ConcurrentQueue<PendingConnectionReply> _pendingConnectionReplies = new();
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
    // Set under _authoritySync by Dispose. Every entry point that touches the runtime checks it
    // under the same lock, so a late ReleaseConnection (a connection loop's finally on a pool
    // thread), message or tick cannot touch the Bullet world while or after it is disposed.
    private bool _disposed;
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
        if (Runtime is { } runtime)
        {
            // Snapshots count the inbox's carried repeat debt in StarvedTicks (W9-2).
            runtime.CarriedRepeatDebtTicks = _commandInbox.GetCarriedRepeatDebtTicks;
        }

        _checkpointWrites =Runtime?.CheckpointWriter is { } checkpointWriter
            ? new BackgroundCheckpointWriter(checkpointWriter, Runtime.PersistenceWrites)
            : null;
    }

    public BackgroundCheckpointWriter? CheckpointWrites => _checkpointWrites;

    public bool DefersJoinsToTick => _deferJoinsToTick;

    // Connections the host still tracks. ReleaseConnection removes one last, under the tick lock,
    // so once this drops the released connection's pending join and seat are gone; a service-level
    // connection count drops before the host's release runs.
    public int ConnectionCount => _connections.Count;

    // How long until the tick runner's next fixed tick is due.
    public double SecondsUntilNextTick =>
        TickPacer.SecondsUntilNextTick(_tickRunner.RemainingSeconds, SimulationClock.FixedDeltaSeconds);

    // Ticks the host's clock skipped after a stall, read from the tick runner itself. Read it on
    // the thread that calls AdvanceTime.
    public long DroppedTicks => _tickRunner.DroppedTicks;

    // Test seam: runs inside ReleaseConnection, under the tick lock, after the released session's
    // claim is confirmed and before its queued actions are purged.
    internal Action<string>? ReleasingSessionForTest { get; set; }

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
        if (Volatile.Read(ref _disposed))
        {
            return Array.Empty<IServerToClientMessage>();
        }

        ServerRoutingContext context = GetOrCreateConnectionContext(connectionId);
        lock (_authoritySync)
        {
            // The context was fetched before the lock. If the connection was released (or the host
            // disposed) in between, the message belongs to a connection that no longer exists: a
            // command routed now would leave a stale ring in the inbox for a session a reconnect
            // may already have claimed.
            if (_disposed
                || !_connections.TryGetValue(connectionId, out ServerRoutingContext? current)
                || !ReferenceEquals(current, context))
            {
                return Array.Empty<IServerToClientMessage>();
            }

            if (_deferJoinsToTick && message is ClientJoinRequestMessage joinRequest)
            {
                QueueJoin(connectionId, joinRequest);
                return Array.Empty<IServerToClientMessage>();
            }

            context.ClearOutboundMessages();
            _router.RouteServerInbound(context, message);
            return context.OutboundMessages.ToArray();
        }
    }

    // Queues an action for a session from the server side (a scripted bot, an admin tool or a
    // test), bypassing the player.action allow-list that keeps clients from sending build and
    // combat use directly. The next tick drains it once the session is joined and validates it
    // like a command-synthesized action. False when the session's pending-action cap is full.
    internal bool TryEnqueueServerAction(PlayerActionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _actionQueue.TryEnqueue(message, out _);
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

    // Server-initiated world resync (a lagging connection's merged deltas overflowed). Unlike
    // HandleClientMessage it never creates a connection context: a connection the host no longer
    // tracks (already released) is skipped instead of coming back as a ghost. The request goes
    // through WorldResyncRequestHandler like a client's, so the same cooldown applies. True when a
    // resync is now pending for the connection; false when the connection is unknown or not
    // joined, or the cooldown refused it (the caller may retry later).
    // Shared by every overflow resync request: the record is immutable, and the service re-asks
    // every tick while the cooldown refuses, so this path must not allocate.
    private static readonly ClientWorldResyncRequestMessage OverflowResyncRequest = new(ClientChecksum: 0);

    public bool RequestWorldResyncIfConnected(string connectionId)
    {
        lock (_authoritySync)
        {
            if (_disposed || !_connections.TryGetValue(connectionId, out ServerRoutingContext? context))
            {
                return false;
            }

            context.ClearOutboundMessages();
            _router.RouteServerInbound(context, OverflowResyncRequest);
            context.ClearOutboundMessages();
            return context.WorldResyncPending;
        }
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
            if (_disposed)
            {
                return 0;
            }

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

    // Replaces the destination's contents with the replies produced during ticks, oldest first:
    // replies to joins applied at tick boundaries (only when the host defers joins to the tick) and
    // world resyncs served at the end of a tick. DedicatedServerService delivers them after
    // AdvanceTime, before the tick's snapshot.
    public void DrainPendingJoinRepliesInto(List<PendingConnectionReply> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        destination.Clear();
        while (_pendingConnectionReplies.TryDequeue(out PendingConnectionReply reply))
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
            ObjectDisposedException.ThrowIf(_disposed, this);
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
            if (!_disposed)
            {
                _ = _authority.ExportSnapshot();
            }
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
            // A fresh claim starts the session's command stream over (the client counts from 1
            // again). The release already removed the ring, but a command routed from the departed
            // connection's context could have recreated it with that connection's high sequence,
            // which would drop the new connection's frames as stale.
            _commandInbox.Remove(new PlayerSessionId(sessionId));
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

    // Releasing a connection runs entirely under the tick lock, where every claim is taken (joins
    // are routed under it, deferred or not). The departed session's queued actions are purged and
    // its runtime seat released before its claim is dropped, so a reconnect that reuses the
    // session id cannot claim it in between and then lose its fresh actions or its new seat.
    //
    // After Dispose only the host's own bookkeeping is dropped: the runtime (and its Bullet world)
    // is gone, and a late release from a connection loop must not touch it.
    public void ReleaseConnection(string connectionId)
    {
        lock (_authoritySync)
        {
            if (_disposed)
            {
                _pendingJoins.RemoveAll(join => string.Equals(join.ConnectionId, connectionId, StringComparison.Ordinal));
                if (_connectionClaims.TryRemove(connectionId, out string? releasedSession))
                {
                    _sessionClaims.TryRemove(new KeyValuePair<string, string>(releasedSession, connectionId));
                }

                _connections.TryRemove(connectionId, out _);
                return;
            }

            if (_deferJoinsToTick)
            {
                for (int i = _pendingJoins.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_pendingJoins[i].ConnectionId, connectionId, StringComparison.Ordinal))
                    {
                        // Its persisted-player load will not be used.
                        Runtime?.DiscardPersistedPlayerPrefetch(_pendingJoins[i].PlayerName);
                        _pendingJoins.RemoveAt(i);
                    }
                }
            }

            if (_connectionClaims.TryGetValue(connectionId, out string? sessionId))
            {
                // Claims are written in pairs, so the owner is this connection; the check keeps a
                // stray release from touching a session another connection holds.
                if (_sessionClaims.TryGetValue(sessionId, out string? ownerConnection)
                    && string.Equals(ownerConnection, connectionId, StringComparison.Ordinal))
                {
                    ReleasingSessionForTest?.Invoke(sessionId);
                    // The departed session's queued actions would otherwise wait forever.
                    _actionQueue.RemoveSession(new PlayerSessionId(sessionId));
                    _commandInbox.Remove(new PlayerSessionId(sessionId));
                    if (_authority is RuntimeAuthoritativeSessionHost runtimeAuthority)
                    {
                        runtimeAuthority.ReleaseSession(sessionId);
                    }

                    _sessionClaims.TryRemove(sessionId, out _);
                }

                _connectionClaims.TryRemove(connectionId, out _);
            }

            if (_connections.TryRemove(connectionId, out _))
            {
                Volatile.Write(ref _connectionsChanged, 1);
            }
        }
    }

    // Takes the tick lock, so it waits for an in-flight tick, message or release (one still
    // removing a Bullet body on a connection loop's thread), and sets _disposed under it: every
    // later entry point sees the flag under the same lock and leaves the runtime alone. The
    // runtime is then disposed outside the lock, which nothing else can enter any more.
    public void Dispose()
    {
        lock (_authoritySync)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
        }

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

        ServePendingWorldResyncs();
    }

    // WorldResyncRequestHandler only marks a connection; the full-world export runs here, on the
    // tick thread after the tick, and the snapshot goes out through the pending-reply drain. With
    // no pending resync this only reads a flag per connection, so steady-state ticks do not allocate.
    private void ServePendingWorldResyncs()
    {
        IWorldResyncSource? source = _authority as IWorldResyncSource;
        for (int i = 0; i < _connectionContexts.Count; i++)
        {
            ServerRoutingContext context = _connectionContexts[i];
            if (!context.TryTakeWorldResyncRequest()
                || source == null
                || context.LastJoinRequest == null
                || !_connections.ContainsKey(context.ConnectionId))
            {
                continue;
            }

            _pendingConnectionReplies.Enqueue(new PendingConnectionReply(
                context.ConnectionId,
                new ServerSnapshotMessage(source.ExportWorldResync())));
        }
    }

    // Queues a join request for the next tick and starts its player's persisted-state load on the
    // persistence worker. Nothing waits for the load here: the receive thread (a thread-pool
    // thread) returns at once, and the tick defers the join until the load finished (see
    // ApplyPendingJoins), so the tick never flushes the write queue or reads the database. The load
    // is read-only, so a join that is never applied creates no profile row. A second join request
    // from a connection whose first one is still pending is ignored: one connection cannot pile up
    // joins (or loads). Called under _authoritySync.
    private void QueueJoin(string connectionId, ClientJoinRequestMessage request)
    {
        for (int i = 0; i < _pendingJoins.Count; i++)
        {
            if (string.Equals(_pendingJoins[i].ConnectionId, connectionId, StringComparison.Ordinal))
            {
                Dream.Multiplayer.Diagnostics.NetworkDiagnosticsLog.Write(
                    "server.join.duplicate-ignored",
                    $"connection={connectionId} session={request.RequestedSessionId}");
                return;
            }
        }

        string playerName = WorldSessionRuntime.ResolveJoinPlayerName(request.RequestedSessionId, request.DisplayName);
        Runtime?.BeginPersistedPlayerPrefetch(playerName);
        _pendingJoins.Add(new PendingJoin(connectionId, request, playerName, DeferredTicks: 0, PrefetchRetried: false));
    }

    // Same-tick joins in canonical order. A join whose connection was dropped is skipped. A join is
    // routed only once its player's persisted state is loaded, checked right before it is routed
    // (an earlier join of the same player in this batch consumes the load, and the later one then
    // waits for a fresh load). Otherwise it waits for a later tick:
    //  - load still pending (or missing, then restarted): deferred, at most
    //    MaxJoinPrefetchDeferralTicks, then rejected with JoinRetryRejectionReason;
    //  - load failed: restarted once, then rejected with JoinRetryRejectionReason.
    // A rejected join leaves the connection open and unclaimed, so the client may send it again.
    // The tick never reads the database. With no pending join this returns before touching
    // anything, so steady-state ticks do not allocate.
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

        WorldSessionRuntime? runtime = Runtime;
        for (int i = 0; i < joins.Count; i++)
        {
            PendingJoin join = joins[i];
            if (!_connections.TryGetValue(join.ConnectionId, out ServerRoutingContext? context))
            {
                runtime?.DiscardPersistedPlayerPrefetch(join.PlayerName);
                continue;
            }

            PersistedPlayerLoadState loadState = runtime?.GetPersistedPlayerLoadState(join.PlayerName)
                ?? PersistedPlayerLoadState.Ready;
            switch (loadState)
            {
                case PersistedPlayerLoadState.Ready:
                    break;
                case PersistedPlayerLoadState.Failed when !join.PrefetchRetried:
                    runtime!.BeginPersistedPlayerPrefetch(join.PlayerName);
                    _pendingJoins.Add(join with { DeferredTicks = join.DeferredTicks + 1, PrefetchRetried = true });
                    continue;
                case PersistedPlayerLoadState.Failed:
                    RejectJoinForRetry(join, "prefetch-failed");
                    continue;
                default:
                    if (join.DeferredTicks >= MaxJoinPrefetchDeferralTicks)
                    {
                        RejectJoinForRetry(join, "prefetch-timeout");
                        continue;
                    }

                    if (loadState == PersistedPlayerLoadState.Missing)
                    {
                        runtime!.BeginPersistedPlayerPrefetch(join.PlayerName);
                    }

                    _pendingJoins.Add(join with { DeferredTicks = join.DeferredTicks + 1 });
                    continue;
            }

            context.ClearOutboundMessages();
            _router.RouteServerInbound(context, join.Request);
            for (int replyIndex = 0; replyIndex < context.OutboundMessages.Count; replyIndex++)
            {
                _pendingConnectionReplies.Enqueue(new PendingConnectionReply(join.ConnectionId, context.OutboundMessages[replyIndex]));
            }

            context.ClearOutboundMessages();
            // An accepted join consumed its load; a rejected one (or one that restored nothing)
            // drops it, so a later join of this player reads fresh rows.
            runtime?.DiscardPersistedPlayerPrefetch(join.PlayerName);
        }

        joins.Clear();
    }

    private void RejectJoinForRetry(PendingJoin join, string reason)
    {
        Runtime?.DiscardPersistedPlayerPrefetch(join.PlayerName);
        _pendingConnectionReplies.Enqueue(new PendingConnectionReply(
            join.ConnectionId,
            new JoinRejectedMessage(JoinRetryRejectionReason)));
        Dream.Multiplayer.Diagnostics.NetworkDiagnosticsLog.Write(
            "server.join.prefetch-rejected",
            $"connection={join.ConnectionId} session={join.Request.RequestedSessionId} player={join.PlayerName} reason={reason} deferredTicks={join.DeferredTicks}");
    }

    private readonly record struct PendingJoin(
        string ConnectionId,
        ClientJoinRequestMessage Request,
        string PlayerName,
        int DeferredTicks,
        bool PrefetchRetried);

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
