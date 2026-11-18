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

        for (ulong sequence = 1; sequence <= 200; sequence++)
        {
            session.QueueCommandFrame(CreateCommand(sequence, jump: false));
        }

        // Not blocking is asserted structurally, not with a stopwatch budget. The gate holds the
        // writer inside the transport's SendAsync for up to 10 s, so if QueueCommandFrame waited
        // on the transport the loop above could not have returned at all. What that leaves to
        // prove is that it really is parked there and has completed nothing: the writer reached
        // the transport, and not one of the 200 queued frames has been sent while the gate is
        // held. The old "ElapsedMilliseconds < 1000" proved neither, and fired on a correct
        // implementation whenever the parallel phase saturated the machine.
        Assert.True(connection.FirstSendStarted.Wait(TimeSpan.FromSeconds(5)), "the writer never reached the transport");
        lock (connection.Sent)
        {
            Assert.Empty(connection.Sent);
        }

        connection.Release();
        Assert.True(await session.FlushOutboundAsync(TimeSpan.FromSeconds(5)));
        lock (connection.Sent)
        {
            Assert.NotEmpty(connection.Sent);
        }
    }

    // NETR-4: commands queued during a short stall are all sent, in order, one per tick; they no
    // longer collapse into the newest one (the server would then lose every tick but the last).
    [Fact]
    public async Task QueuedCommandsWhileStalled_AreAllSentInOrder()
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
        Assert.Equal([1ul, 2ul, 3ul, 4ul], commands.Select(command => command.CommandFrame.Sequence));
        Assert.Equal([false, true, false, false], commands.Select(command => command.CommandFrame.MoveIntent.JumpPressed));
        Assert.Equal(0, session.MergedCommandCount);
    }

    // A stall longer than the 16-command queue merges the oldest commands instead of dropping
    // them: the jump and the primary use pressed early in the stall survive, the merged command
    // carries the newest sequence it absorbed, and everything still goes out in order.
    [Fact]
    public async Task QueuedCommandsBeyondCapacity_MergeOldestKeepingPressesAndNewestSequence()
    {
        GatedTransportConnection connection = new();
        await using RemoteClientSession session = CreateSession(connection);

        session.QueueCommandFrame(CreateCommand(1, jump: false));
        Assert.True(connection.FirstSendStarted.Wait(TimeSpan.FromSeconds(5)));
        const ulong last = 41;
        for (ulong sequence = 2; sequence <= last; sequence++)
        {
            session.QueueCommandFrame(CreateCommand(sequence, jump: sequence == 3, primaryUse: sequence == 5));
        }

        Assert.Equal(ClientOutboundCommandQueue.DefaultCapacity, session.PendingCommandCount);
        connection.Release();
        Assert.True(await session.FlushOutboundAsync(TimeSpan.FromSeconds(5)));

        PlayerCommandFrame[] commands = connection.Sent.OfType<PlayerCommandMessage>().Select(message => message.CommandFrame).ToArray();
        Assert.Equal(1 + ClientOutboundCommandQueue.DefaultCapacity, commands.Length);
        Assert.Equal(1ul, commands[0].Sequence);
        // Command 2 stays alone: folding the jump frame (3) into it would move the jump a tick
        // earlier (wave 8 fix loop 2, S-A). Commands 3..27 merged into one that starts at the jump's
        // tick and keeps the jump and primary use; 28..41 follow unmerged.
        ulong firstUnmerged = last - (ulong)ClientOutboundCommandQueue.DefaultCapacity + 3;
        Assert.Equal(2ul, commands[1].Sequence);
        Assert.False(commands[1].MoveIntent.JumpPressed || commands[1].PrimaryUsePressed);
        Assert.Equal(firstUnmerged - 1, commands[2].Sequence);
        Assert.True(commands[2].MoveIntent.JumpPressed);
        Assert.True(commands[2].PrimaryUsePressed);
        Assert.Equal(
            Enumerable.Range((int)firstUnmerged, (int)(last - firstUnmerged + 1)).Select(value => (ulong)value),
            commands.Skip(3).Select(command => command.Sequence));
        Assert.All(commands.Skip(3), command => Assert.False(command.MoveIntent.JumpPressed || command.PrimaryUsePressed));
        Assert.Equal((long)(last - 1 - (ulong)ClientOutboundCommandQueue.DefaultCapacity), session.MergedCommandCount);
    }

    [Fact]
    public async Task QueuedCommands_ForAnotherSessionOrNotNewer_AreIgnored()
    {
        GatedTransportConnection connection = new();
        connection.Release();
        await using RemoteClientSession session = CreateSession(connection);

        session.QueueCommandFrame(CreateCommand(5, jump: false));
        session.QueueCommandFrame(CreateCommand(5, jump: true));
        session.QueueCommandFrame(CreateCommand(4, jump: true));
        session.QueueCommandFrame(CreateCommand(6, jump: false) with { SessionId = new PlayerSessionId("session-b") });
        session.QueueCommandFrame(CreateCommand(7, jump: false));
        Assert.True(await session.FlushOutboundAsync(TimeSpan.FromSeconds(5)));

        PlayerCommandFrame[] commands = connection.Sent.OfType<PlayerCommandMessage>().Select(message => message.CommandFrame).ToArray();
        Assert.Equal([5ul, 7ul], commands.Select(command => command.Sequence));
        Assert.All(commands, command => Assert.False(command.MoveIntent.JumpPressed));
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

    private static PlayerCommandFrame CreateCommand(ulong sequence, bool jump, bool primaryUse = false)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId("session-a"),
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0f, 0f, JumpPressed: jump),
            PlayerLookIntent.None,
            PrimaryUsePressed: primaryUse);
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
