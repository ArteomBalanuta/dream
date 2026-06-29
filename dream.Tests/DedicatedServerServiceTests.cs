using Xunit;

public class DedicatedServerServiceTests
{
    [Fact]
    public async Task AdvanceOnceAsync_BroadcastsSnapshotToAttachedConnectionsWhenTickExecutes()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        FakeTickDrivenAuthority authority = new();
        DedicatedServerHost host = CreateHost(authority);
        await using FakeServerTransport transport = new();
        await using DedicatedServerService service = new(host, transport, codec, framer);
        FakeTransportConnection connection = new();
        ServerConnection serverConnection = new(connection, host, codec, framer);
        service.AttachConnection(serverConnection);
        host.HandleClientMessage(connection.ConnectionId, new ClientJoinRequestMessage("session-a", "player-a"));

        int executedTicks = await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        DecodedProtocolMessage outbound = connection.SingleOutbound(codec, framer);

        Assert.Equal(1, executedTicks);
        ServerSnapshotMessage snapshot = Assert.IsType<ServerSnapshotMessage>(outbound.Message);
        Assert.Equal((ulong)1, snapshot.Snapshot.ServerTick);
    }

    [Fact]
    public async Task AdvanceOnceAsync_DoesNotBroadcastSnapshotToUnjoinedConnections()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        FakeTickDrivenAuthority authority = new();
        DedicatedServerHost host = CreateHost(authority);
        await using FakeServerTransport transport = new();
        await using DedicatedServerService service = new(host, transport, codec, framer);
        FakeTransportConnection connection = new();
        service.AttachConnection(new ServerConnection(connection, host, codec, framer));

        int executedTicks = await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);

        Assert.Equal(1, executedTicks);
        Assert.Empty(connection.OutboundFrames);
    }

    private static DedicatedServerHost CreateHost(ITickDrivenAuthoritativeSessionHost authority)
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(authority, router);
    }

    private sealed class FakeServerTransport : IServerTransport
    {
        public int Port => 5057;

        public void Start()
        {
        }

        public ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTransportConnection : ITransportConnection
    {
        private readonly List<byte[]> _outbound = [];

        public string ConnectionId => "broadcast-connection";

        public IReadOnlyList<byte[]> OutboundFrames => _outbound;

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(0);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            _outbound.Add(payload.ToArray());
            return ValueTask.CompletedTask;
        }

        public DecodedProtocolMessage SingleOutbound(TransportJsonCodec codec, LengthPrefixedMessageFramer framer)
        {
            byte[] framed = Assert.Single(_outbound);
            bool complete = framer.TryDeframe(framed, out byte[]? payload, out int consumed);
            Assert.True(complete);
            Assert.Equal(framed.Length, consumed);
            Assert.NotNull(payload);
            return codec.Decode(payload);
        }

        public ValueTask DisposeAsync()
        {
            _outbound.Clear();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTickDrivenAuthority : ITickDrivenAuthoritativeSessionHost
    {
        public ulong CurrentServerTick { get; private set; }

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            return new JoinAcceptedMessage(requestedSessionId, 101, CurrentServerTick, InitialWorld: null);
        }

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
        {
            return new AuthoritativeActionResult(
                message.SessionId.Value,
                message.ActionSequence,
                CurrentServerTick,
                Accepted: true,
                Payload: new WorldMutationResult(
                    WorldMutationKind.CubePlaced,
                    new OpenTK.Mathematics.Vector3(4f, 1f, 4f),
                    Applied: true));
        }

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false)
        {
            return new ServerSnapshotEnvelope(
                CurrentServerTick,
                new Dictionary<string, ulong>(),
                Array.Empty<ActorSnapshot>(),
                PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>());
        }

        public void AdvanceTick(
            ulong serverTick,
            float deltaTime,
            IReadOnlyList<PlayerCommandFrame> commandFrames,
            PlayerSimulationTuning playerTuning,
            bool allowGameplaySimulation = true)
        {
            CurrentServerTick = serverTick;
        }
    }
}
