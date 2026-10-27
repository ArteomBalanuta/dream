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
public class TickPacerTests
{
    [Fact]
    public void SecondsUntilNextTick_IsTickMinusAccumulator()
    {
        Assert.Equal(0.010, TickPacer.SecondsUntilNextTick(0.005, 0.015), 6);
        Assert.Equal(0d, TickPacer.SecondsUntilNextTick(0.020, 0.015));
    }

    [Fact]
    public void WaitFor_OvershootsDeadlineByOnlyAFewMilliseconds()
    {
        using TickPacer pacer = new();
        double totalOvershoot = 0d;
        const int waits = 20;
        for (int index = 0; index < waits; index++)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            pacer.WaitFor(SimulationClock.FixedDeltaSeconds, CancellationToken.None);
            totalOvershoot += stopwatch.Elapsed.TotalSeconds - SimulationClock.FixedDeltaSeconds;
        }

        Assert.InRange(totalOvershoot / waits, 0d, 0.004);
    }

    [Fact]
    public void PacedLoop_RunsOneTickPerIterationAlmostAlways()
    {
        DedicatedServerHost host = new(new CountingAuthority(), new ProtocolRouter([], []));
        using TickPacer pacer = new();
        Stopwatch stopwatch = Stopwatch.StartNew();
        int singleTickIterations = 0;
        const int iterations = 64;
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

        Assert.True(singleTickIterations >= iterations * 0.85, $"Only {singleTickIterations}/{iterations} iterations ran exactly one tick.");
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
