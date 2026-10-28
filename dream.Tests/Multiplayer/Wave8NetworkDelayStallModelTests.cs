using Dream.Client.Replication;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Multiplayer;

// Wave 8 fix loop 2 (reviewer S-D): a network stall, not a send-loop stall. The kernel socket
// buffer absorbs seconds of ~150 B command frames, so the client's managed send queue stays empty
// and ObserveSendBacklog never fires, while the server starves, repeats the last command for
// MaxCommandRepeatTicks and idles. The ack-lag trigger (ObserveAcknowledgedSequence) is the only
// thing that can tell the client.
//
// Tick-exact model: the real ClientCommandTickStream (64 Hz, one command per tick) and the real
// BufferedPlayerCommandInbox, with the runtime's NETR-3 rule; the uplink and downlink are delay
// lines in ticks, and a stall holds every uplink frame sent during it until it ends (delivered
// in order, in one burst, as TCP would). A tick "walks" when the simulated command moves; the
// error at a snapshot is the client's walk ticks up to the acked sequence minus the server's walk
// ticks, measured like the reconciler only when the ack advances. One walk tick is 0.094 m
// (6 m/s), so the 3 m snap distance is 32 ticks.
public class Wave8NetworkDelayStallModelTests(ITestOutputHelper output)
{
    private const int WalkTicksBeforeStall = 96;
    private const int TicksAfterStall = 320;
    private const float WalkMetresPerTick = 6f / SimulationClock.AuthoritativeTicksPerSecond;
    private static readonly int SnapTicks = (int)Math.Ceiling(ClientPredictionReconciler.DefaultSnapDistance / WalkMetresPerTick);
    private static readonly PlayerSessionId Session = new("w8-net-delay");

    // About a second of uplink delay with nothing queued locally: the client switches to idle once
    // the ack lag passes the round trip by the repeat window, sends idle from there, and the
    // correction stays far below the snap distance (it walked about one round trip of ticks past
    // the server's switch, which the server then replays from the backlog).
    [Theory]
    [InlineData(64, 2, 2)]
    [InlineData(64, 6, 7)]
    [InlineData(80, 3, 3)]
    public void UplinkHeldForAboutASecond_AckTriggerIdles_CorrectionBelowSnap(int stallTicks, int uplinkTicks, int downlinkTicks)
    {
        ModelResult result = Run(stallTicks, uplinkTicks, downlinkTicks, observeAcks: true, jitterTicks: 0, seed: 1);
        output.WriteLine(result.Describe($"{stallTicks}-tick uplink hold, {uplinkTicks}+{downlinkTicks} tick RTT"));

        Assert.True(result.AckStallIdleTicks > 0, result.Describe("no ack-stall idle"));
        Assert.True(result.MaxErrorTicks < SnapTicks, result.Describe("snap"));
        // Every tick the client walked was walked by the server too, the stall's 16 repeats
        // included: the late idle switch is replayed, not lost.
        Assert.InRange(result.FinalErrorTicks, -2, 2);
    }

    // The same stall without the ack trigger: the client walks through it, and the walk ticks past
    // the server's repeat window are replayed behind the stall's latency until the shed folds them,
    // a snap-sized correction. This is what the trigger fixes.
    [Fact]
    public void UplinkHeldForASecond_WithoutTheAckTrigger_EndsSnapDistanceBehind()
    {
        ModelResult result = Run(64, 2, 2, observeAcks: false, jitterTicks: 0, seed: 1);
        output.WriteLine(result.Describe("64-tick uplink hold, no ack trigger"));

        Assert.Equal(0, result.SendStallIdleTicks);
        Assert.True(result.MaxErrorTicks >= SnapTicks, result.Describe("expected a snap without the trigger"));
    }

    // Steady play at a 200 ms round trip (13 ticks), with or without a few ticks of jitter, never
    // idles the player: the baseline is that round trip.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    public void SteadyPlayAt200MsRtt_NeverTriggersIdle(int jitterTicks, int seed)
    {
        ModelResult result = Run(stallTicks: 0, uplinkTicks: 6, downlinkTicks: 7, observeAcks: true, jitterTicks, seed, totalTicks: 64 * 60);
        output.WriteLine(result.Describe($"steady 200 ms RTT, jitter {jitterTicks}"));

        Assert.Equal(0, result.SendStallIdleTicks);
        // Jitter alone starves the server now and then (the jitter buffer's job, unrelated to the
        // trigger): a few ticks at most, 3 measured at 4 ticks of jitter, with or without the
        // fix loop 2 skip-debt changes.
        Assert.InRange(result.MaxErrorTicks, 0, jitterTicks);
        Assert.InRange(result.Baseline, 12d, 14d + jitterTicks);
    }

    private static ModelResult Run(
        int stallTicks,
        int uplinkTicks,
        int downlinkTicks,
        bool observeAcks,
        int jitterTicks,
        int seed,
        int totalTicks = WalkTicksBeforeStall + 80 + TicksAfterStall)
    {
        Random random = new(seed);
        ClientCommandTickStream stream = new(Session);
        ulong?[] clientAck = [null];
        if (observeAcks)
        {
            stream.ObserveAcknowledgedSequence(() => clientAck[0]);
        }

        BufferedPlayerCommandInbox inbox = new();
        // Uplink: frames with their arrival tick, in send order (TCP keeps the order).
        Queue<(long Arrival, PlayerCommandFrame Frame)> uplink = new();
        // Downlink: snapshots (ack, server walk ticks at that ack) with their arrival tick.
        Queue<(long Arrival, ulong Ack, int ServerWalk)> downlink = new();
        Dictionary<ulong, bool> clientWalked = [];
        List<PlayerCommandFrame> commands = [];
        long stallStart = WalkTicksBeforeStall;
        long stallEnd = stallStart + stallTicks;
        long lastUplinkArrival = 0;
        long lastDownlinkArrival = 0;
        PlayerCommandFrame? lastApplied = null;
        int starved = 0;
        int serverWalk = 0;
        ulong serverAck = 0;
        ulong lastComparedAck = 0;
        int maxError = 0;
        int finalError = 0;
        long clientWalkCount = 0;
        double tickSeconds = 1d / SimulationClock.AuthoritativeTicksPerSecond;

        for (long tick = 1; tick <= totalTicks; tick++)
        {
            // Client: snapshots that arrived, then one prediction tick and its command.
            while (downlink.Count > 0 && downlink.Peek().Arrival <= tick)
            {
                (_, ulong ack, int walkAtAck) = downlink.Dequeue();
                clientAck[0] = ack;
                if (ack != lastComparedAck)
                {
                    lastComparedAck = ack;
                    int clientWalkAtAck = clientWalked.Count(entry => entry.Key <= ack && entry.Value);
                    finalError = clientWalkAtAck - walkAtAck;
                    maxError = Math.Max(maxError, Math.Abs(finalError));
                }
            }

            stream.CaptureInput(Walk(0));
            commands.Clear();
            stream.Advance(tickSeconds + 1e-9, commands, static (list, command, _) => list.Add(command));
            foreach (PlayerCommandFrame command in commands)
            {
                bool walks = command.MoveIntent.ForwardAxis != 0f;
                clientWalked[command.Sequence] = walks;
                clientWalkCount += walks ? 1 : 0;
                long arrival = tick + uplinkTicks + Jitter(random, jitterTicks);
                if (tick >= stallStart && tick < stallEnd)
                {
                    arrival = Math.Max(arrival, stallEnd + uplinkTicks);
                }

                // In order: a frame never overtakes the one sent before it.
                arrival = Math.Max(arrival, lastUplinkArrival);
                lastUplinkArrival = arrival;
                uplink.Enqueue((arrival, command));
            }

            // Server: enqueue what arrived, pop one tick, NETR-3 when starved.
            while (uplink.Count > 0 && uplink.Peek().Arrival <= tick)
            {
                inbox.Enqueue(uplink.Dequeue().Frame);
            }

            PlayerCommandFrame simulated;
            if (inbox.TryPop(Session, out PlayerCommandFrame popped))
            {
                lastApplied = popped;
                serverAck = popped.Sequence;
                starved = 0;
                simulated = popped;
            }
            else if (lastApplied.HasValue)
            {
                starved++;
                simulated = starved > WorldSessionRuntime.MaxCommandRepeatTicks
                    ? WorldSessionRuntime.CreateStarvedCommandFrame(lastApplied.Value)
                    : lastApplied.Value;
            }
            else
            {
                continue;
            }

            serverWalk += simulated.MoveIntent.ForwardAxis != 0f ? 1 : 0;
            long snapshotArrival = Math.Max(tick + downlinkTicks + Jitter(random, jitterTicks), lastDownlinkArrival);
            lastDownlinkArrival = snapshotArrival;
            downlink.Enqueue((snapshotArrival, serverAck, serverWalk));
        }

        return new ModelResult(
            maxError,
            finalError,
            stream.SendStallIdleTicks,
            stream.AckStallIdleTicks,
            stream.AckLagBaselineTicks,
            clientWalkCount,
            serverWalk);
    }

    private static int Jitter(Random random, int jitterTicks)
    {
        return jitterTicks == 0 ? 0 : random.Next(0, jitterTicks + 1);
    }

    private static PlayerCommandFrame Walk(ulong sequence)
    {
        return new PlayerCommandFrame(
            Session,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0f),
            new PlayerLookIntent(0f, 0f, Vector3.UnitZ),
            PrimaryUsePressed: false);
    }

    private sealed record ModelResult(
        int MaxErrorTicks,
        int FinalErrorTicks,
        long SendStallIdleTicks,
        long AckStallIdleTicks,
        double Baseline,
        long ClientWalkTicks,
        int ServerWalkTicks)
    {
        public string Describe(string label) =>
            $"{label}: max error {MaxErrorTicks} ticks ({MaxErrorTicks * WalkMetresPerTick:F2} m), final {FinalErrorTicks}, " +
            $"idle ticks {SendStallIdleTicks} (ack-only {AckStallIdleTicks}), baseline {Baseline:F2}, " +
            $"client walk {ClientWalkTicks}, server walk {ServerWalkTicks}";
    }
}
