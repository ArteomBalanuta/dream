using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Replication;

// NETR-4: the client sends exactly one command per 64 Hz prediction tick, its sequence the tick.
// Before, it numbered a command per render frame and sent one only on ticks where a fresh frame
// had arrived: about 30 per second at 30 fps, and sequence gaps at 144 fps.
public class ClientCommandTickStreamTests
{
    private static readonly PlayerSessionId Session = new("session-a");

    [Theory]
    [InlineData(30)]
    [InlineData(64)]
    [InlineData(144)]
    public async Task AtAnyRenderRate_TheSessionSends64ConsecutiveCommandsPerSecond(int framesPerSecond)
    {
        const int Seconds = 5;
        CapturingConnection connection = new();
        await using RemoteClientSession session = new(
            connection,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 5057, Session.Value, "player-a", "player-a"));
        ClientCommandTickStream stream = new(Session);
        double frameSeconds = 1d / framesPerSecond;
        List<int> commandsPerSecond = [];
        int commandsThisSecond = 0;

        for (int second = 0; second < Seconds; second++)
        {
            for (int frame = 0; frame < framesPerSecond; frame++)
            {
                stream.CaptureInput(Walk(jump: false));
                commandsThisSecond += stream.Advance(
                    frameSeconds,
                    session,
                    static (target, command, _) => target.QueueCommandFrame(command));
                // Simulated time runs far faster than real time; let the send loop keep pace so
                // the bounded queue never has to merge (that path is covered elsewhere).
                if (session.PendingCommandCount >= ClientOutboundCommandQueue.DefaultCapacity / 2)
                {
                    Assert.True(await session.FlushOutboundAsync(TimeSpan.FromSeconds(10)));
                }
            }

            commandsPerSecond.Add(commandsThisSecond);
            commandsThisSecond = 0;
        }

        Assert.True(await session.FlushOutboundAsync(TimeSpan.FromSeconds(10)));
        Assert.All(commandsPerSecond, count => Assert.Equal(SimulationClock.AuthoritativeTicksPerSecond, count));
        ulong[] sent = connection.SentCommands().Select(command => command.Sequence).ToArray();
        Assert.Equal(
            Enumerable.Range(1, SimulationClock.AuthoritativeTicksPerSecond * Seconds).Select(value => (ulong)value),
            sent);
        Assert.Equal((ulong)(SimulationClock.AuthoritativeTicksPerSecond * Seconds), stream.LastTick);
        Assert.Equal(0, stream.DroppedTicks);
        Assert.Equal(0, session.MergedCommandCount);
    }

    // A slow frame catches up one command per elapsed tick, up to the per-advance cap; the time
    // past the cap is dropped rather than replayed, and the sequences stay consecutive.
    [Fact]
    public void SlowFrame_CatchesUpOneCommandPerTick_UpToTheCap()
    {
        ClientCommandTickStream stream = new(Session);
        List<PlayerCommandFrame> commands = [];

        stream.CaptureInput(Walk(jump: false));
        int caughtUp = stream.Advance(3d / SimulationClock.AuthoritativeTicksPerSecond + 1e-6, commands, static (list, command, _) => list.Add(command));
        stream.CaptureInput(Walk(jump: false));
        int capped = stream.Advance(1d, commands, static (list, command, _) => list.Add(command));

        Assert.Equal(3, caughtUp);
        Assert.Equal(SimulationClock.DefaultMaxTicksPerAdvance, capped);
        Assert.Equal(SimulationClock.AuthoritativeTicksPerSecond - SimulationClock.DefaultMaxTicksPerAdvance, stream.DroppedTicks);
        Assert.Equal(
            Enumerable.Range(1, 3 + SimulationClock.DefaultMaxTicksPerAdvance).Select(value => (ulong)value),
            commands.Select(command => command.Sequence));
    }

    // At 144 fps most frames run no tick. A jump captured in such a frame survives the next
    // frame's input (without the press) and reaches the next tick's command, and only that one:
    // ticks without fresh input repeat the held axes, not the press.
    [Fact]
    public void PressCapturedBetweenTicks_ReachesExactlyOneCommand()
    {
        ClientCommandTickStream stream = new(Session);
        List<PlayerCommandFrame> commands = [];
        double frameSeconds = 1d / 144d;

        stream.CaptureInput(Walk(jump: false));
        Assert.Equal(0, stream.Advance(frameSeconds, commands, static (list, command, _) => list.Add(command)));
        stream.CaptureInput(Walk(jump: true) with { RequestedMode = new PlayerModeSwitchIntent(PlayerInteractionMode.Combat) });
        Assert.Equal(0, stream.Advance(frameSeconds, commands, static (list, command, _) => list.Add(command)));
        stream.CaptureInput(Walk(jump: false));
        Assert.Equal(1, stream.Advance(frameSeconds, commands, static (list, command, _) => list.Add(command)));
        // A frame stall: three ticks with no new input.
        Assert.Equal(3, stream.Advance(3d / SimulationClock.AuthoritativeTicksPerSecond, commands, static (list, command, _) => list.Add(command)));

        Assert.Equal(4, commands.Count);
        Assert.True(commands[0].MoveIntent.JumpPressed);
        Assert.Equal(PlayerInteractionMode.Combat, commands[0].RequestedMode.Mode);
        Assert.All(commands.Skip(1), command =>
        {
            Assert.False(command.MoveIntent.JumpPressed);
            Assert.Null(command.RequestedMode.Mode);
            Assert.Equal(1f, command.MoveIntent.ForwardAxis);
            Assert.Equal(Vector3.UnitZ, command.LookIntent.Facing);
        });
        Assert.Equal([1ul, 2ul, 3ul, 4ul], commands.Select(command => command.Sequence).ToArray());
    }

    // Held primary use is not a one-shot: it keeps repeating like the axes, so a held build or
    // attack keeps its server-side cadence on ticks without a fresh frame.
    [Fact]
    public void HeldPrimaryUse_RepeatsOnTicksWithoutFreshInput()
    {
        ClientCommandTickStream stream = new(Session);
        List<PlayerCommandFrame> commands = [];

        stream.CaptureInput(Walk(jump: false) with { PrimaryUsePressed = true });
        stream.Advance(4d / SimulationClock.AuthoritativeTicksPerSecond + 1e-6, commands, static (list, command, _) => list.Add(command));

        Assert.Equal(4, commands.Count);
        Assert.All(commands, command => Assert.True(command.PrimaryUsePressed));
    }

    [Fact]
    public void Advance_WithoutAnyCapturedInput_SendsIdleCommandsForTheFallbackSession()
    {
        ClientCommandTickStream stream = new(Session);
        List<PlayerCommandFrame> commands = [];

        stream.Advance(2d / SimulationClock.AuthoritativeTicksPerSecond + 1e-6, commands, static (list, command, _) => list.Add(command));

        Assert.Equal(
            [PlayerCommandFrame.CreateIdle(Session, 1), PlayerCommandFrame.CreateIdle(Session, 2)],
            commands.ToArray());
    }

    [Fact]
    public void CaptureAndAdvance_AllocateNothing()
    {
        ClientCommandTickStream stream = new(Session);
        PlayerCommandFrame input = Walk(jump: false);
        int[] ticks = [0];
        // Warm up the static lambda and the generic instantiation.
        stream.CaptureInput(input);
        stream.Advance(1d / 30d, ticks, static (counter, _, _) => counter[0]++);

        AllocationAssert.AtMost(0, () =>
        {
            stream.CaptureInput(input);
            stream.Advance(1d / 30d, ticks, static (counter, _, _) => counter[0]++);
        });
    }

    private static PlayerCommandFrame Walk(bool jump)
    {
        return new PlayerCommandFrame(
            Session,
            0,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0f, 0f, JumpPressed: jump),
            new PlayerLookIntent(0.25f, 0f, Vector3.UnitZ),
            PrimaryUsePressed: false);
    }

    private sealed class CapturingConnection : ITransportConnection
    {
        private readonly TransportJsonCodec _codec = new();
        private readonly LengthPrefixedMessageFramer _framer = new();
        private readonly List<PlayerCommandFrame> _commands = [];

        public string ConnectionId => "capturing";

        public IReadOnlyList<PlayerCommandFrame> SentCommands()
        {
            lock (_commands)
            {
                return _commands.ToArray();
            }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Assert.True(_framer.TryDeframe(payload.Span, out byte[]? message, out _));
            if (_codec.Decode(message!).Message is PlayerCommandMessage command)
            {
                lock (_commands)
                {
                    _commands.Add(command.CommandFrame);
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(0);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
