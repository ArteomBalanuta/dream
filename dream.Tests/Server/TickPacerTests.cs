using System.Diagnostics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Server;

// The server loop slept with Task.Delay(1) (~15.6 ms on Windows) against a 15.625 ms tick, so
// iterations alternated between 0 and 2 ticks and snapshots arrived irregularly.
[Collection(TimingSensitiveCollection.Name)]
public class TickPacerTests
{
    [Fact]
    public void SecondsUntilNextTick_IsTickMinusAccumulator()
    {
        Assert.Equal(0.010, TickPacer.SecondsUntilNextTick(0.005, 0.015), 6);
        Assert.Equal(0d, TickPacer.SecondsUntilNextTick(0.020, 0.015));
    }

    // Every wait is sampled, and the statistic is the SMALLEST overshoot of the batch.
    //
    // Nothing softer survives a loaded machine. TimingSensitive only serialises tests inside this
    // process; the rest of a loaded suite, or another process, can still take the CPU away in the
    // middle of a wait, and a stolen slice is worth 10-15 ms. Measured here, a batch of waits loses
    // between 3% and 90% of its samples to that depending on what else the box is doing, so every
    // statistic that reads a share of the samples - the mean, the median, a percentile, a count of
    // good waits, the longest unbroken run of them - moves with the load and not with the pacer.
    // The median is what this test used to assert, and a full-suite run duly read 9.1 ms from it
    // against a 4 ms bound while the same test, run alone, read 1.5 MICROseconds.
    //
    // The minimum is the one statistic load cannot fake, because being descheduled only ever ADDS
    // time to a wait. Across every run measured - idle, and with the cores oversubscribed four
    // times over - the smallest overshoot in a batch stayed between 0.1 and 0.8 microseconds, with
    // no reading in between: either a wait is interrupted and loses milliseconds, or it lands on
    // the deadline. So the batch minimum is the pacer's own floor, and the bound belongs just above
    // it.
    //
    // That is what pays for the bound being TIGHTENED from 4 ms to 10 microseconds rather than
    // loosened. The pacer this test exists to prevent - the Task.Delay(1) loop, which wakes on the
    // next 15.6 ms timer edge instead of on the deadline it was given - scatters its overshoots
    // across the whole 15.6 ms period, so it can only sneak under a 10 us bound by landing one wait
    // in roughly 1500 on the edge by luck. It cannot do what this test now demands at all: land on
    // the deadline itself.
    private const int MeasuredWaits = 128;

    // Above the ~0.8 us floor measured under four-times CPU oversubscription, and 1500x below the
    // 15.6 ms timer period a regressed pacer would round up to.
    private const double DeadlineToleranceSeconds = 0.00001;

    [Fact]
    public void WaitFor_OvershootsDeadlineByOnlyAFewMilliseconds()
    {
        using TickPacer pacer = new();
        double[] overshoots = new double[MeasuredWaits];
        for (int index = 0; index < MeasuredWaits; index++)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            pacer.WaitFor(SimulationClock.FixedDeltaSeconds, CancellationToken.None);
            overshoots[index] = stopwatch.Elapsed.TotalSeconds - SimulationClock.FixedDeltaSeconds;
        }

        Array.Sort(overshoots);
        double best = overshoots[0];

        // Waking EARLY is a pacer bug rather than CPU starvation - load can only delay a wait - so
        // a negative sample fails on its own terms, with its own message.
        Assert.True(best >= 0d, $"WaitFor returned {-best * 1000d:F4} ms before its deadline.");
        Assert.True(
            best <= DeadlineToleranceSeconds,
            $"The closest of {MeasuredWaits} waits still overshot its deadline by {best * 1000d:F4} ms; "
            + $"the bound is {DeadlineToleranceSeconds * 1000d:F3} ms. "
            + $"(median {overshoots[MeasuredWaits / 2] * 1000d:F4} ms, worst {overshoots[MeasuredWaits - 1] * 1000d:F4} ms - "
            + "those two move with the machine's load, the minimum does not.)");
    }

    // Left measuring a share of the samples, unlike the test above, because here that share does
    // not move with the load: the pacer is asked for the time remaining in the current tick, so a
    // stolen slice is absorbed by the clock's accumulator and only costs an iteration when it runs
    // past a SECOND tick, 31.25 ms away. Measured under the same four-times oversubscription that
    // drops 90% of the fixed-deadline waits above, every round of this loop still ran 64/64
    // iterations at exactly one tick. The best of MaxAttempts rounds is kept so that one round
    // losing the CPU outright does not fail it.
    private const int MaxAttempts = 3;

    [Fact]
    public void PacedLoop_RunsOneTickPerIterationAlmostAlways()
    {
        const int iterations = 64;
        int bestSingleTickIterations = 0;
        for (int attempt = 0; attempt < MaxAttempts && bestSingleTickIterations < iterations * 0.85; attempt++)
        {
            bestSingleTickIterations = Math.Max(bestSingleTickIterations, CountSingleTickIterations(iterations));
        }

        Assert.True(
            bestSingleTickIterations >= iterations * 0.85,
            $"Only {bestSingleTickIterations}/{iterations} iterations ran exactly one tick (best of up to {MaxAttempts} runs).");
    }

    private static int CountSingleTickIterations(int iterations)
    {
        DedicatedServerHost host = new(new CountingAuthority(), new ProtocolRouter([], []));
        using TickPacer pacer = new();
        Stopwatch stopwatch = Stopwatch.StartNew();
        int singleTickIterations = 0;
        for (int index = 0; index < iterations; index++)
        {
            pacer.WaitFor(host.SecondsUntilNextTick, CancellationToken.None);
            double elapsed = stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();
            if (host.AdvanceTime(elapsed) == 1)
            {
                singleTickIterations++;
            }
        }

        return singleTickIterations;
    }

    private sealed class CountingAuthority : ITickDrivenAuthoritativeSessionHost
    {
        public ulong CurrentServerTick { get; private set; }

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            return new JoinAcceptedMessage(requestedSessionId, 1, CurrentServerTick, InitialWorld: null);
        }

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
        {
            return new AuthoritativeActionResult(message.SessionId.Value, message.ActionSequence, CurrentServerTick, true, null);
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

        public IReadOnlyList<AuthoritativeActionResult> AdvanceTick(
            ulong serverTick,
            float deltaTime,
            IReadOnlyList<PlayerCommandFrame> commandFrames,
            IReadOnlyList<QueuedPlayerAction> queuedActions,
            PlayerSimulationTuning playerTuning,
            bool allowGameplaySimulation = true)
        {
            CurrentServerTick = serverTick;
            return Array.Empty<AuthoritativeActionResult>();
        }
    }
}
