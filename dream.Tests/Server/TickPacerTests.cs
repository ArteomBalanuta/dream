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

    // TimingSensitive only serialises tests inside this process; another process can still starve
    // the CPU. Each test measures up to MaxAttempts times and asserts the unchanged bound on the
    // best run, so one starved run does not fail it but a pacer that is really slow still does.
    private const int MaxAttempts = 3;

    [Fact]
    public void WaitFor_OvershootsDeadlineByOnlyAFewMilliseconds()
    {
        using TickPacer pacer = new();
        const int waits = 20;
        // An early wake (negative overshoot) is a pacer bug, not CPU starvation, so "best" is the
        // smallest non-negative mean; a run that woke early is kept only if no run was on time.
        double bestMeanOvershoot = double.NaN;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            double totalOvershoot = 0d;
            for (int index = 0; index < waits; index++)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                pacer.WaitFor(SimulationClock.FixedDeltaSeconds, CancellationToken.None);
                totalOvershoot += stopwatch.Elapsed.TotalSeconds - SimulationClock.FixedDeltaSeconds;
            }

            double meanOvershoot = totalOvershoot / waits;
            if (double.IsNaN(bestMeanOvershoot)
                || (meanOvershoot >= 0d && (bestMeanOvershoot < 0d || meanOvershoot < bestMeanOvershoot)))
            {
                bestMeanOvershoot = meanOvershoot;
            }

            if (bestMeanOvershoot is >= 0d and <= 0.004)
            {
                break;
            }
        }

        Assert.InRange(bestMeanOvershoot, 0d, 0.004);
    }

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
