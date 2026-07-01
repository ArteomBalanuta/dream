using System.Collections.Concurrent;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Server;

public sealed class DedicatedServerHost : IDisposable, IServerSessionClaimRegistry
{
    private static readonly PlayerSimulationTuning DefaultServerTuning = new(
        EyeHeightOffset: 0.35f,
        MouseSensitivity: 2.5f,
        MoveSpeed: 6f,
        NoclipSpeed: 18f,
        JumpVelocity: 5f);

    private readonly IProtocolRouter _router;
    private readonly IAuthoritativeSessionHost? _authority;
    private readonly ITickDrivenAuthoritativeSessionHost? _tickDrivenAuthority;
    private readonly IPlayerCommandInbox _commandInbox = new LatestPlayerCommandInbox();
    private readonly ServerTickRunner _tickRunner = new();
    private readonly ConcurrentDictionary<string, ServerRoutingContext> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionClaims = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _connectionClaims = new(StringComparer.Ordinal);
    private readonly object _authoritySync = new();

    public DedicatedServerHost(
        IAuthoritativeSessionHost? authority,
        IProtocolRouter router)
    {
        _authority = authority;
        _tickDrivenAuthority = authority as ITickDrivenAuthoritativeSessionHost;
        _router = router;
    }

    public WorldSessionRuntime? Runtime => (_authority as RuntimeAuthoritativeSessionHost)?.Runtime;

    public static DedicatedServerHost CreateDefault()
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler()
            ],
            Array.Empty<IClientMessageHandler>());

        return new DedicatedServerHost(authority: null, router);
    }

    public static DedicatedServerHost CreateRuntimeAttached()
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler()
            ],
            Array.Empty<IClientMessageHandler>());

        return new DedicatedServerHost(RuntimeAuthoritativeSessionHost.CreateDefault(), router);
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
                IReadOnlyList<PlayerCommandFrame> pendingCommands = DrainJoinedCommands();
                tickDrivenAuthority.AdvanceTick(
                    tick,
                    fixedDeltaSeconds,
                    pendingCommands,
                    DefaultServerTuning,
                    allowGameplaySimulation: true);
            });
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
        }
    }

    public void Dispose()
    {
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
                state.SessionClaims),
            (Authority: _authority, CommandInbox: _commandInbox, SessionClaims: (IServerSessionClaimRegistry)this));
    }

    private IReadOnlyList<PlayerCommandFrame> DrainJoinedCommands()
    {
        PlayerSessionId[] joinedSessionIds = _connections.Values
            .Select(context => context.LastJoinRequest)
            .Where(join => join != null)
            .Select(join => new PlayerSessionId(join!.RequestedSessionId))
            .ToArray();
        return _commandInbox.Drain(joinedSessionIds);
    }
}
