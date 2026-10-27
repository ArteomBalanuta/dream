using System.Collections.Concurrent;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Server;

public sealed class DedicatedServerHost : IDisposable, IServerSessionClaimRegistry
{
    private static readonly PlayerSimulationTuning DefaultServerTuning = PlayerSimulationTuning.AuthoritativeDefault;

    private readonly IProtocolRouter _router;
    private readonly IAuthoritativeSessionHost? _authority;
    private readonly ITickDrivenAuthoritativeSessionHost? _tickDrivenAuthority;
    private readonly IPlayerCommandInbox _commandInbox = new LatestPlayerCommandInbox();
    private readonly IPlayerActionQueue _actionQueue = new PlayerActionQueue();
    private readonly ServerTickRunner _tickRunner = new();
    private readonly ConcurrentDictionary<string, ServerRoutingContext> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionClaims = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _connectionClaims = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<AuthoritativeActionResult> _pendingActionResults = new();
    private readonly object _authoritySync = new();
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
        ITickDrivenAuthoritativeSessionHost tickDrivenAuthority = _tickDrivenAuthority
            ?? throw new InvalidOperationException(
                "Cannot advance server time before a runtime-backed authoritative session host is attached.");

        lock (_authoritySync)
        {
            return _tickRunner.Advance(elapsedSeconds, (tick, fixedDeltaSeconds) =>
            {
                PlayerSessionId[] joinedSessionIds = ResolveJoinedSessionIds();
                IReadOnlyList<PlayerCommandFrame> pendingCommands = _commandInbox.Drain(joinedSessionIds);
                IReadOnlyList<QueuedPlayerAction> pendingActions = _actionQueue.Drain(joinedSessionIds);
                IReadOnlyList<AuthoritativeActionResult> actionResults = tickDrivenAuthority.AdvanceTick(
                    tick,
                    fixedDeltaSeconds,
                    pendingCommands,
                    pendingActions,
                    DefaultServerTuning,
                    allowGameplaySimulation: true);
                foreach (AuthoritativeActionResult actionResult in actionResults)
                {
                    _pendingActionResults.Enqueue(actionResult);
                }

                if (Runtime != null)
                {
                    _checkpointElapsedSeconds += fixedDeltaSeconds;
                    while (_checkpointElapsedSeconds >= 5d)
                    {
                        // Capture is in-memory; the database write runs off the tick thread.
                        if (_checkpointWrites != null &&
                            Runtime.CapturePersistenceCheckpoint() is { } checkpoint)
                        {
                            _checkpointWrites.Enqueue(checkpoint);
                        }

                        _checkpointElapsedSeconds -= 5d;
                    }
                }
            });
        }
    }

    public IReadOnlyList<AuthoritativeActionResult> DrainPendingActionResults()
    {
        List<AuthoritativeActionResult> drained = [];
        while (_pendingActionResults.TryDequeue(out AuthoritativeActionResult actionResult))
        {
            drained.Add(actionResult);
        }

        return drained;
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

        _connections.TryRemove(connectionId, out _);
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
        return _connections.GetOrAdd(
            connectionId,
            static (id, state) => new ServerRoutingContext(
                id,
                state.Authority,
                state.CommandInbox,
                state.ActionQueue,
                state.SessionClaims),
            (Authority: _authority, CommandInbox: _commandInbox, ActionQueue: _actionQueue, SessionClaims: (IServerSessionClaimRegistry)this));
    }

    private PlayerSessionId[] ResolveJoinedSessionIds()
    {
        return _connections.Values
            .Select(context => context.LastJoinRequest)
            .Where(join => join != null)
            .Select(join => new PlayerSessionId(join!.RequestedSessionId))
            .ToArray();
    }
}
