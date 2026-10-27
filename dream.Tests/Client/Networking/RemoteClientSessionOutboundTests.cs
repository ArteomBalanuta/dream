using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Networking;

// PlayScene used to send one command per render frame with a blocking send on the render
// thread: ~110–250 commands/s for a server that consumes 64, and a frozen frame whenever the
// socket buffer filled.
public class RemoteClientSessionOutboundTests
{
    [Fact]
    public async Task QueueCommandFrame_DoesNotBlockWhileTransportIsStalled()
    {
        GatedTransportConnection connection = new();
        await using RemoteClientSession session = CreateSession(connection);

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (ulong sequence = 1; sequence <= 200; sequence++)
        {
            session.QueueCommandFrame(CreateCommand(sequence, jump: false));
        }

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"Queueing took {stopwatch.ElapsedMilliseconds} ms.");
        connection.Release();
        Assert.True(await session.FlushOutboundAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task QueuedCommandsWhileStalled_CollapseIntoOneMergedCommand()
    {
        GatedTransportConnection connection = new();
        await using RemoteClientSession session = CreateSession(connection);

        session.QueueCommandFrame(CreateCommand(1, jump: false));
        Assert.True(connection.FirstSendStarted.Wait(TimeSpan.FromSeconds(5)));
        session.QueueCommandFrame(CreateCommand(2, jump: true));
        session.QueueCommandFrame(CreateCommand(3, jump: false));
        session.QueueCommandFrame(CreateCommand(4, jump: false));
        connection.Release();
        Assert.True(await session.FlushOutboundAsync(TimeSpan.FromSeconds(5)));

        PlayerCommandMessage[] commands = connection.Sent.OfType<PlayerCommandMessage>().ToArray();
        Assert.Equal(2, commands.Length);
        Assert.Equal(1ul, commands[0].CommandFrame.Sequence);
        Assert.Equal(4ul, commands[1].CommandFrame.Sequence);
        Assert.True(commands[1].CommandFrame.MoveIntent.JumpPressed);
    }

    [Fact]
    public async Task QueuedActions_AreSentInOrderBeforeTheMergedCommand()
    {
        GatedTransportConnection connection = new();
        connection.Release();
        await using RemoteClientSession session = CreateSession(connection);

        session.QueuePlayerAction(CreateAction(10));
        session.QueuePlayerAction(CreateAction(11));
        session.QueueCommandFrame(CreateCommand(5, jump: false));
        Assert.True(await session.FlushOutboundAsync(TimeSpan.FromSeconds(5)));

        Assert.Collection(
            connection.Sent,
            message => Assert.Equal(10ul, Assert.IsType<PlayerActionMessage>(message).ActionSequence),
            message => Assert.Equal(11ul, Assert.IsType<PlayerActionMessage>(message).ActionSequence),
            message => Assert.Equal(5ul, Assert.IsType<PlayerCommandMessage>(message).CommandFrame.Sequence));
    }

    private static RemoteClientSession CreateSession(ITransportConnection connection)
    {
        return new RemoteClientSession(
            connection,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 5057, "session-a", "player-a", "player-a"));
    }

    private static PlayerCommandFrame CreateCommand(ulong sequence, bool jump)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId("session-a"),
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0f, 0f, JumpPressed: jump),
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
    }

    private static PlayerActionMessage CreateAction(ulong sequence)
    {
        return new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            sequence,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(10, 2f, 10)));
    }

    private sealed class GatedTransportConnection : ITransportConnection
    {
        private readonly ManualResetEventSlim _gate = new();
        private readonly TransportJsonCodec _codec = new();
        private readonly LengthPrefixedMessageFramer _framer = new();

        public string ConnectionId => "gated";

        public ManualResetEventSlim FirstSendStarted { get; } = new();

        public List<IProtocolMessage> Sent { get; } = [];

        public void Release()
        {
            _gate.Set();
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            FirstSendStarted.Set();
            _gate.Wait(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.True(_framer.TryDeframe(payload.Span, out byte[]? message, out _));
            lock (Sent)
            {
                Sent.Add(_codec.Decode(message!).Message);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(0);
        }

        public ValueTask DisposeAsync()
        {
            _gate.Set();
            return ValueTask.CompletedTask;
        }
    }
}
