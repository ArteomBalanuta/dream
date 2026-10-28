using System.Diagnostics;
using Dream.Simulation.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Load;

// PHYS-2 measurement: runs a load scenario while sampling how many cube bodies the server's
// physics simulates each tick (CubeRestFreezer.LastAwakeBodyCount) and how many are frozen.
// Skipped unless DREAM_LOAD_TEST=1. For the "before" figures run it with
// DREAM_CUBE_REST_FREEZE=0, which turns freezing off (the pre-PHYS-2 simulation).
[Collection(TimingSensitiveCollection.Name)]
public class CubeRestFreezerLoadMeasurementTests
{
    private readonly ITestOutputHelper _output;

    public CubeRestFreezerLoadMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LoadFact]
    public Task Smoke8_AwakeCubeBodies()
    {
        return MeasureAsync(LoadScenario.Smoke8);
    }

    [LoadFact]
    public Task Players32_AwakeCubeBodies()
    {
        return MeasureAsync(LoadScenario.Players32);
    }

    private async Task MeasureAsync(LoadScenario scenario)
    {
        scenario = scenario.WithEnvironmentOverrides();
        List<(double Seconds, int Awake, int AwakeCubes, int Frozen)> samples = [];
        using CancellationTokenSource stop = new();
        Stopwatch clock = Stopwatch.StartNew();
        Task sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                samples.Add((clock.Elapsed.TotalSeconds, CubeRestFreezer.LastAwakeBodyCount, CubeRestFreezer.LastAwakeCubeBodyCount, CubeRestFreezer.LastFrozenCount));
                try
                {
                    await Task.Delay(50, stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        LoadReport report = await LoadRunner.RunAsync(scenario, line => _output.WriteLine(line));
        stop.Cancel();
        await sampler;

        // The last two thirds of the run: the cubes restored at startup have settled by then.
        double from = clock.Elapsed.TotalSeconds - (scenario.Duration.TotalSeconds * 2d / 3d);
        int[] awake = samples.Where(sample => sample.Seconds >= from).Select(sample => sample.Awake).OrderBy(value => value).ToArray();
        int[] awakeCubes = samples.Where(sample => sample.Seconds >= from).Select(sample => sample.AwakeCubes).OrderBy(value => value).ToArray();
        int[] frozen = samples.Where(sample => sample.Seconds >= from).Select(sample => sample.Frozen).OrderBy(value => value).ToArray();
        Assert.NotEmpty(awake);
        _output.WriteLine(
            $"{scenario.Name}: freezing {(CubeRestFreezer.DefaultEnabled ? "on" : "off")}; " +
            $"awake map bodies p50 {Percentile(awake, 0.5)} p99 {Percentile(awake, 0.99)} max {awake[^1]}; " +
            $"of them placed cubes p50 {Percentile(awakeCubes, 0.5)} p99 {Percentile(awakeCubes, 0.99)} max {awakeCubes[^1]}; " +
            $"frozen p50 {Percentile(frozen, 0.5)}; " +
            $"advance per tick p50 {report.Tick.AdvancePerTick.P50Milliseconds:0.000} ms p99 {report.Tick.AdvancePerTick.P99Milliseconds:0.000} ms; " +
            $"loop p50 {report.Tick.SteadyState.P50Milliseconds:0.000} ms p99 {report.Tick.SteadyState.P99Milliseconds:0.000} ms; " +
            $"ticks/s {report.Tick.TicksPerSecond:0.0}; ticks per loop iteration {report.Tick.TicksPerAdvance:0.00}; dropped {report.Tick.DroppedTicks}");
    }

    private static int Percentile(int[] sorted, double fraction)
    {
        return sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
