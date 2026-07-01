using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Server;


public class ServerConnectionTests
{
    [Fact]
    public async Task ProcessIncomingAsync_DecodesJoinRequestAndSendsJoinAcceptedReply()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        FakeTickDrivenAuthority authority = new();
        DedicatedServerHost host = CreateHost(authority);
        FakeTransportConnection transport = new();
        ServerConnection connection = new(transport, host, codec, framer);

        await transport.QueueInboundAsync(codec, framer, new ClientJoinRequestMessage("session-a", "player-a"), sequence: 1);

        bool keepOpen = await connection.ProcessIncomingAsync();
        DecodedProtocolMessage reply = transport.SingleOutbound(codec, framer);

        Assert.True(keepOpen);
        JoinAcceptedMessage accepted = Assert.IsType<JoinAcceptedMessage>(reply.Message);
        Assert.Equal("session-a", accepted.SessionId);
        Assert.Equal((ulong)1, reply.Envelope.Sequence);
    }

    [Fact]
    public async Task SendSnapshotAsync_FramesAndWritesSnapshotMessage()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        FakeTickDrivenAuthority authority = new();
        DedicatedServerHost host = CreateHost(authority);
        FakeTransportConnection transport = new();
        ServerConnection connection = new(transport, host, codec, framer);

        await connection.SendSnapshotAsync(new ServerSnapshotMessage(new ServerSnapshotEnvelope(
            7,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>())));

        DecodedProtocolMessage reply = transport.SingleOutbound(codec, framer);
        ServerSnapshotMessage snapshot = Assert.IsType<ServerSnapshotMessage>(reply.Message);
        Assert.Equal((ulong)7, snapshot.Snapshot.ServerTick);
        Assert.Equal((ulong)1, reply.Envelope.Sequence);
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

    private sealed class FakeTransportConnection : ITransportConnection
    {
        private readonly Queue<byte[]> _inbound = new();
        private readonly List<byte[]> _outbound = [];

        public string ConnectionId => "fake-connection";

        public ValueTask DisposeAsync()
        {
            _inbound.Clear();
            _outbound.Clear();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_inbound.Count == 0)
            {
                return 0;
            }

            byte[] payload = _inbound.Dequeue();
            payload.CopyTo(buffer);
            return payload.Length;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            _outbound.Add(payload.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask QueueInboundAsync<TMessage>(
            TransportJsonCodec codec,
            LengthPrefixedMessageFramer framer,
            TMessage message,
            ulong sequence)
            where TMessage : IProtocolMessage
        {
            byte[] payload = codec.Encode(
                new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence),
                message);
            _inbound.Enqueue(framer.Frame(payload));
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
    }

    private sealed class FakeTickDrivenAuthority : ITickDrivenAuthoritativeSessionHost
    {
        private string _primarySessionId = PlayerSessionId.LocalDefault.Value;

        public ulong CurrentServerTick { get; private set; }

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            _primarySessionId = requestedSessionId;
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
