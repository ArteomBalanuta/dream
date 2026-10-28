using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Replication;

// Wave 8 fix loop 2 (reviewer S-D): the ack-lag trigger of the client's send-stall idle mirror.
public class ClientCommandTickStreamAckStallTests
{
    private static readonly PlayerSessionId Session = new("ack-stall");
    private static readonly double TickSeconds = 1d / SimulationClock.AuthoritativeTicksPerSecond;

    // A steady round trip of 10 ticks, then the server stops accepting at sequence 100 (the
    // uplink stalled). The client idles once the lag passes the baseline by the repeat window,
    // and walks again as soon as the ack catches up.
    [Fact]
    public void AckStopsAdvancing_IdlesPastTheRoundTripPlusRepeatWindow_AndWalksAgainWhenItResumes()
    {
        const int RoundTripTicks = 10;
        const ulong FrozenAck = 100;
        ClientCommandTickStream stream = new(Session);
        ulong?[] ack = [null];
        stream.ObserveAcknowledgedSequence(() => ack[0]);
        List<PlayerCommandFrame> commands = [];

        for (ulong tick = 1; tick <= 220; tick++)
        {
            // The ack the client holds when it produces command `tick`.
            ulong latest = tick > RoundTripTicks ? tick - RoundTripTicks : 0;
            ack[0] = latest == 0 ? null : tick < 180 ? Math.Min(latest, FrozenAck) : latest;
            stream.CaptureInput(Walk());
            stream.Advance(TickSeconds + 1e-9, commands, static (list, command, _) => list.Add(command));
        }

        Assert.Equal(220, commands.Count);
        ulong[] idle = commands.Where(command => command.MoveIntent.ForwardAxis == 0f).Select(command => command.Sequence).ToArray();
        Assert.NotEmpty(idle);
        // The lag passes RoundTripTicks + 16 once tick - 100 > 26 (the baseline rose by less than
        // a tick meanwhile).
        Assert.InRange(idle[0], FrozenAck + RoundTripTicks + BufferedPlayerCommandInbox.ServerRepeatTicks + 1, FrozenAck + RoundTripTicks + BufferedPlayerCommandInbox.ServerRepeatTicks + 3);
        Assert.Equal(179UL, idle[^1]);
        Assert.Equal(idle.Length, (int)(idle[^1] - idle[0] + 1));
        Assert.Equal(idle.Length, stream.AckStallIdleTicks);
        Assert.Equal(idle.Length, stream.SendStallIdleTicks);
        // Facing kept.
        Assert.All(commands, command => Assert.Equal(Vector3.UnitZ, command.LookIntent.Facing));
    }

    // A round trip that grows slowly (a few ticks per second) is followed by the baseline and
    // never idles the player; neither does a lag that sits at a constant 200 ms.
    [Theory]
    [InlineData(13, 0d)]
    [InlineData(13, 0.05d)]
    [InlineData(40, 0d)]
    public void SteadyOrSlowlyGrowingRoundTrip_NeverIdles(int roundTripTicks, double growthPerTick)
    {
        ClientCommandTickStream stream = new(Session);
        ulong?[] ack = [null];
        stream.ObserveAcknowledgedSequence(() => ack[0]);
        List<PlayerCommandFrame> commands = [];
        double roundTrip = roundTripTicks;
        for (ulong tick = 1; tick <= 64 * 30; tick++)
        {
            roundTrip += growthPerTick;
            ulong lag = (ulong)roundTrip;
            ack[0] = tick > lag ? tick - lag : null;
            stream.CaptureInput(Walk());
            stream.Advance(TickSeconds + 1e-9, commands, static (list, command, _) => list.Add(command));
        }

        Assert.Equal(0, stream.SendStallIdleTicks);
    }

    // A new session's acks start over below the last one; the baseline restarts with them.
    [Fact]
    public void AckGoingBackwards_RestartsTheBaseline()
    {
        ClientCommandTickStream stream = new(Session);
        ulong?[] ack = [null];
        stream.ObserveAcknowledgedSequence(() => ack[0]);
        List<PlayerCommandFrame> commands = [];
        for (ulong tick = 1; tick <= 200; tick++)
        {
            // Lag 5 until tick 100, then a rejoin whose acks restart at 1 (lag 99, steady).
            ack[0] = tick <= 100 ? tick - 5 : tick - 99;
            stream.CaptureInput(Walk());
            stream.Advance(TickSeconds + 1e-9, commands, static (list, command, _) => list.Add(command));
        }

        Assert.Equal(0, stream.SendStallIdleTicks);
        Assert.Equal(99d, stream.AckLagBaselineTicks);
    }

    [Fact]
    public void AcknowledgedSequence_ReadsTheSessionsAckFromASnapshot()
    {
        ServerSnapshotEnvelope snapshot = default(ServerSnapshotEnvelope) with
        {
            LastAcceptedCommandSequences = new Dictionary<string, ulong> { ["ack-stall"] = 42 }
        };

        Assert.Equal(42UL, ClientCommandTickStream.AcknowledgedSequence(snapshot, "ack-stall"));
        Assert.Null(ClientCommandTickStream.AcknowledgedSequence(snapshot, "other"));
        Assert.Null(ClientCommandTickStream.AcknowledgedSequence(null, "ack-stall"));
    }

    [Fact]
    public void AdvanceWithBothProbes_AllocatesNothing()
    {
        ClientCommandTickStream stream = new(Session);
        int[] unsent = [0];
        ulong?[] ack = [1];
        stream.ObserveSendBacklog(() => unsent[0]);
        stream.ObserveAcknowledgedSequence(() => ack[0]);
        PlayerCommandFrame input = Walk();
        int[] ticks = [0];
        stream.CaptureInput(input);
        stream.Advance(1d / 30d, ticks, static (counter, _, _) => counter[0]++);

        AllocationAssert.AtMost(0, () =>
        {
            stream.CaptureInput(input);
            stream.Advance(1d / 30d, ticks, static (counter, _, _) => counter[0]++);
        });
    }

    private static PlayerCommandFrame Walk()
    {
        return new PlayerCommandFrame(
            Session,
            0,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0f),
            new PlayerLookIntent(0f, 0f, Vector3.UnitZ),
            PrimaryUsePressed: false);
    }
}
