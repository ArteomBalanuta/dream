using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Load;

// Headless load scenarios against the production server over loopback. Skipped unless
// DREAM_LOAD_TEST=1 (see LoadFactAttribute); DREAM_LOAD_DURATION_SECONDS shortens every scenario.
// Each run writes artifacts/load/<scenario>.json and .md: those numbers are the baseline later
// scalability work is measured against. Run them in Release (dotnet test -c Release); Debug
// numbers are several times worse.
//
// Thresholds are generous regression guards set from the first baseline (2026-10-28, Release,
// 16 logical CPUs), about 1.5-2x the measured figures. They are not the targets: the round-2
// target is a tick-loop p99 under TargetTickP99Milliseconds, which no scenario meets yet
// (Smoke8 measured 21-42 ms, because the simulation itself costs ~10 ms per tick with 2k
// dynamic cubes and the loop runs two ticks per iteration). Tighten the guards as SCALE-* lands.
[Collection(TimingSensitiveCollection.Name)]
public class ServerLoadTests
{
    public const double TargetTickP99Milliseconds = 10d;

    private const double RealTimeTicksPerSecond = 64d;

    private readonly ITestOutputHelper _output;

    public ServerLoadTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Baseline: loop p99 21-42 ms, per-tick advance p99 11-17 ms, 64 ticks/s, ~150 KB/s per
    // client. A run while other heavy processes shared the machine measured loop p99 310 ms.
    [LoadFact]
    public async Task Smoke8()
    {
        LoadReport report = await RunAsync(LoadScenario.Smoke8);

        AssertHealthy(report, minimumTicksPerSecond: RealTimeTicksPerSecond * 0.9d);
        AssertTickP99(report, loopP99Milliseconds: 60d, perTickP99Milliseconds: 30d);
    }

    // NETR-7: compares snapshot intervals (DREAM_SNAPSHOT_INTERVAL_TICKS=1 or 2) where the loop
    // runs about one tick per iteration. Guards as Smoke8's.
    [LoadFact]
    public async Task Light8()
    {
        LoadReport report = await RunAsync(LoadScenario.Light8);

        AssertHealthy(report, minimumTicksPerSecond: RealTimeTicksPerSecond * 0.9d);
        AssertTickP99(report, loopP99Milliseconds: 60d, perTickP99Milliseconds: 30d);
    }

    // Baseline: loop p99 ~35 ms, per-tick advance p99 ~14 ms, 64 ticks/s, ~725 KB/s per client.
    [LoadFact]
    public async Task Players32()
    {
        LoadReport report = await RunAsync(LoadScenario.Players32);

        AssertHealthy(report, minimumTicksPerSecond: RealTimeTicksPerSecond * 0.9d);
        AssertTickP99(report, loopP99Milliseconds: 75d, perTickP99Milliseconds: 30d);
    }

    // Baseline: the server cannot keep real time with 10k dynamic cubes (~90 ms per tick, ~12
    // ticks/s, the clock drops the rest). The guard only catches it getting much worse.
    [LoadFact]
    public async Task World10k()
    {
        LoadReport report = await RunAsync(LoadScenario.World10k);

        // One snapshot per loop iteration: about one per second while the server is this far behind.
        AssertHealthy(report, minimumTicksPerSecond: 6d, minimumSnapshotsPerSecond: 0.5d);
        Assert.True(
            report.Startup.RestoredPlacedCubes >= report.Seed.PlacedCubes * 0.95d,
            $"Restored {report.Startup.RestoredPlacedCubes} of {report.Seed.PlacedCubes} seeded cubes.");
        Assert.True(report.Startup.LoadMilliseconds < 30_000d, $"Startup took {report.Startup.LoadMilliseconds:0} ms.");
        AssertTickP99(report, loopP99Milliseconds: 2_000d, perTickP99Milliseconds: 250d);
    }

    // 30 minutes of churn: a bot leaves and a new session joins every few seconds. Managed memory
    // after a forced full GC must stay within 5% of the post-warm-up figure. Each join currently
    // stalls the tick for 250-550 ms (join work runs on the tick; SCALE-3), so a 40 s trial ran
    // 45 ticks/s; the real-time guard is relaxed accordingly.
    [LoadFact]
    public async Task Soak()
    {
        LoadReport report = await RunAsync(LoadScenario.Soak);

        AssertHealthy(report, minimumTicksPerSecond: 30d);
        Assert.True(report.Join.ChurnReconnects > 0, "The soak never reconnected a bot.");
        long baseline = report.Memory.PostWarmUpForcedGcBytes;
        long final = report.Memory.FinalForcedGcBytes;
        Assert.True(
            final <= baseline * 1.05d,
            $"Managed memory after full GC grew from {baseline / 1048576d:0.0} MB to {final / 1048576d:0.0} MB (more than 5%).");
    }

    private async Task<LoadReport> RunAsync(LoadScenario scenario)
    {
        scenario = scenario.WithEnvironmentOverrides();
        LoadReport report = await LoadRunner.RunAsync(scenario, line => _output.WriteLine(line));
        _output.WriteLine(LoadReportMarkdown.Render(report));
        _output.WriteLine(
            $"Target tick-loop p99 {TargetTickP99Milliseconds:0} ms (not asserted): measured {report.Tick.SteadyState.P99Milliseconds:0.00} ms.");
        return report;
    }

    private static void AssertTickP99(LoadReport report, double loopP99Milliseconds, double perTickP99Milliseconds)
    {
        Assert.True(
            report.Tick.SteadyState.P99Milliseconds < loopP99Milliseconds,
            $"{report.Scenario} tick-loop p99 {report.Tick.SteadyState.P99Milliseconds:0.00} ms; guard {loopP99Milliseconds:0} ms.");
        Assert.True(
            report.Tick.AdvancePerTick.P99Milliseconds < perTickP99Milliseconds,
            $"{report.Scenario} advance per tick p99 {report.Tick.AdvancePerTick.P99Milliseconds:0.00} ms; guard {perTickP99Milliseconds:0} ms.");
    }

    private static void AssertHealthy(LoadReport report, double minimumTicksPerSecond, double minimumSnapshotsPerSecond = 5d)
    {
        Assert.Equal(0, report.Join.ConnectFailures);
        Assert.Equal(0, report.Join.Rejected);
        Assert.True(
            report.Join.JoinRequests == report.Join.Joined,
            $"{report.Join.Joined} of {report.Join.JoinRequests} bots joined; server connection errors: " +
            string.Join("; ", report.Join.ServerConnectionErrorReasons));
        Assert.Equal(0, report.Persistence.CheckpointWritesFailed);
        Assert.Equal(0, report.Persistence.PersistenceWritesFailed);
        Assert.True(
            report.Tick.TicksPerSecond >= minimumTicksPerSecond,
            $"Server ran {report.Tick.TicksPerSecond:0.0} ticks/s; guard {minimumTicksPerSecond:0.0}.");
        if (minimumTicksPerSecond >= RealTimeTicksPerSecond * 0.9d)
        {
            Assert.True(
                report.Tick.DroppedTicks <= Math.Max(8, report.Tick.SimulatedTicks / 100),
                $"Dropped {report.Tick.DroppedTicks} of {report.Tick.SimulatedTicks} ticks.");
        }

        Assert.True(
            report.Network.BotSnapshotsPerSecondMean >= minimumSnapshotsPerSecond,
            $"Bots received {report.Network.BotSnapshotsPerSecondMean:0.0} snapshots/s on average.");
    }
}
