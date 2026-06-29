public sealed class DedicatedServerHost : IDisposable
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
    private readonly Dictionary<string, ServerRoutingContext> _connections = new(StringComparer.Ordinal);

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
        context.ClearOutboundMessages();
        _router.RouteServerInbound(context, message);
        return context.OutboundMessages.ToArray();
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

    public ServerSnapshotMessage CreateSnapshotMessage(bool includeWorldSnapshot = false)
    {
        if (_authority == null)
        {
            throw new InvalidOperationException(
                "Cannot export a server snapshot before an authoritative session host is attached.");
        }

        return new ServerSnapshotMessage(_authority.ExportSnapshot(includeWorldSnapshot));
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
        if (_connections.TryGetValue(connectionId, out ServerRoutingContext? existing))
        {
            return existing;
        }

        ServerRoutingContext created = new(connectionId, _authority, _commandInbox);
        _connections[connectionId] = created;
        return created;
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
