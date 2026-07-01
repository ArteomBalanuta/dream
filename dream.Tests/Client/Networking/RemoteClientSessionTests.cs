using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Client.Networking;


public class RemoteClientSessionTests
{
    [Fact]
    public async Task ConnectAsync_SendsHelloThenJoinRequest()
    {
        FakeClientTransport transport = new();
        RemoteClientOptions options = new("127.0.0.1", 5057, "session-a", "player-a", "player-a");

        await using RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            options,
            transport,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer());

        Assert.Equal("session-a", session.SessionId);
        Assert.Collection(
            transport.Connection.OutboundMessages,
            message => Assert.IsType<ClientHelloMessage>(message),
            message =>
            {
                ClientJoinRequestMessage join = Assert.IsType<ClientJoinRequestMessage>(message);
                Assert.Equal("session-a", join.RequestedSessionId);
            });
    }

    [Fact]
    public async Task SendCommandFrameAsync_EncodesPlayerCommandMessage()
    {
        FakeTransportConnection connection = new();
        await using RemoteClientSession session = new(
            connection,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 5057, "session-a", "player-a", "player-a"));

        await session.SendCommandFrameAsync(new PlayerCommandFrame(
            new PlayerSessionId("session-a"),
            7,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
            new PlayerMoveIntent(1f, 0f, 0f),
            new PlayerLookIntent(0f, 0f),
            PrimaryUsePressed: false));

        PlayerCommandMessage command = Assert.IsType<PlayerCommandMessage>(Assert.Single(connection.OutboundMessages));
        Assert.Equal("session-a", command.CommandFrame.SessionId.Value);
        Assert.Equal((ulong)7, command.CommandFrame.Sequence);
    }

    [Fact]
    public async Task SendPlayerActionAsync_EncodesPlayerActionMessage()
    {
        FakeTransportConnection connection = new();
        await using RemoteClientSession session = new(
            connection,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 5057, "session-a", "player-a", "player-a"));

        await session.SendPlayerActionAsync(new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            8,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(14, 3f, 12))));

        PlayerActionMessage action = Assert.IsType<PlayerActionMessage>(Assert.Single(connection.OutboundMessages));
        Assert.Equal("session-a", action.SessionId.Value);
        Assert.Equal((ulong)8, action.ActionSequence);
        PlayerStatsUpdateIntent update = Assert.IsType<PlayerStatsUpdateIntent>(action.Payload);
        Assert.Equal(14, update.Stats.Strength);
    }

    [Fact]
    public async Task ProcessInboundOnceAsync_QueuesDecodedServerMessage()
    {
        FakeTransportConnection connection = new();
        RemoteClientSession session = new(
            connection,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 5057, "session-a", "player-a", "player-a"));
        await using (session)
        {
            connection.QueueInbound(new ServerSnapshotMessage(new ServerSnapshotEnvelope(
                9,
                new Dictionary<string, ulong>(),
                Array.Empty<ActorSnapshot>(),
                PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>())));

            bool keepOpen = await session.ProcessInboundOnceAsync();

            Assert.True(keepOpen);
            Assert.True(session.TryDequeueInbound(out IServerToClientMessage? message));
            ServerSnapshotMessage snapshot = Assert.IsType<ServerSnapshotMessage>(message);
            Assert.Equal((ulong)9, snapshot.Snapshot.ServerTick);
        }
    }

    private sealed class FakeClientTransport : IClientTransport
    {
        public FakeTransportConnection Connection { get; } = new();

        public ValueTask<ITransportConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<ITransportConnection>(Connection);
        }
    }

    private sealed class FakeTransportConnection : ITransportConnection
    {
        private readonly Queue<byte[]> _inbound = new();
        private readonly TransportJsonCodec _codec = new();
        private readonly LengthPrefixedMessageFramer _framer = new();

        public string ConnectionId => "remote-client-test";

        public List<IProtocolMessage> OutboundMessages { get; } = [];

        public ValueTask DisposeAsync()
        {
            _inbound.Clear();
            OutboundMessages.Clear();
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_inbound.Count == 0)
            {
                return ValueTask.FromResult(0);
            }

            byte[] payload = _inbound.Dequeue();
            payload.CopyTo(buffer);
            return ValueTask.FromResult(payload.Length);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            bool complete = _framer.TryDeframe(payload.Span, out byte[]? messagePayload, out _);
            Assert.True(complete);
            Assert.NotNull(messagePayload);
            OutboundMessages.Add(_codec.Decode(messagePayload).Message);
            return ValueTask.CompletedTask;
        }

        public void QueueInbound(IServerToClientMessage message)
        {
            byte[] payload = message switch
            {
                ServerHelloMessage hello => _codec.Encode(
                    new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, hello.MessageKind, 1),
                    hello),
                JoinAcceptedMessage joinAccepted => _codec.Encode(
                    new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, joinAccepted.MessageKind, 1),
                    joinAccepted),
                JoinRejectedMessage joinRejected => _codec.Encode(
                    new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, joinRejected.MessageKind, 1),
                    joinRejected),
                ServerSnapshotMessage snapshot => _codec.Encode(
                    new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, snapshot.MessageKind, 1),
                    snapshot),
                ServerActionMessage action => _codec.Encode(
                    new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, action.MessageKind, 1),
                    action),
                _ => throw new InvalidOperationException()
            };
            _inbound.Enqueue(_framer.Frame(payload));
        }
    }
}
