using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Presentation;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Client.Presentation;

// NETR-7 sends snapshots every 2 ticks (32 Hz) by default. Remote actors are interpolated by
// server tick (ServerTickClock + TickInterpolationBuffer), so the wider spacing must only widen
// the interpolation delay by about a tick: no backwards rendering, no frames past the newest
// sample, and the same accuracy on a constant-velocity walk as at 64 Hz.
public class SnapshotIntervalInterpolationTests(ITestOutputHelper output)
{
    private const double TickSeconds = 1d / SimulationClock.AuthoritativeTicksPerSecond;
    private const float MetresPerTick = 4.5f / SimulationClock.AuthoritativeTicksPerSecond;

    [Theory]
    [InlineData(144, 0)]
    [InlineData(144, 6)]
    [InlineData(60, 6)]
    [InlineData(240, 12)]
    public void ThirtyTwoHertzSnapshots_RenderSmoothly_LikeSixtyFourHertz(int framesPerSecond, int jitterMilliseconds)
    {
        Run perTick = Simulate(intervalTicks: 1, framesPerSecond, jitterMilliseconds, seed: 7);
        Run everyOther = Simulate(intervalTicks: 2, framesPerSecond, jitterMilliseconds, seed: 7);
        output.WriteLine($"{framesPerSecond} fps, jitter {jitterMilliseconds} ms");
        output.WriteLine($"  interval 1: {perTick}");
        output.WriteLine($"  interval 2: {everyOther}");

        foreach (Run run in new[] { perTick, everyOther })
        {
            Assert.Equal(0, run.BackwardsRenderTicks);
            Assert.Equal(0, run.BackwardsPositions);
            Assert.Equal(0, run.FramesPastNewestSample);
            // Linear motion interpolates exactly between samples.
            Assert.True(run.MaxErrorMetres < 0.01f, run.ToString());
        }

        // The 32 Hz delay is wider by about the extra tick of spacing, never by more than 2 ticks,
        // and stays inside the clock's bounds.
        Assert.InRange(everyOther.MeanDelayTicks - perTick.MeanDelayTicks, 0d, 2.5d);
        Assert.True(everyOther.MeanDelayTicks <= ServerTickClock.MaxInterpolationDelayTicks);
        // The rendered step per frame stays as even as at 64 Hz.
        Assert.True(everyOther.MaxStepMetres <= (perTick.MaxStepMetres * 1.5f) + 0.005f, $"{everyOther.MaxStepMetres} vs {perTick.MaxStepMetres}");
    }

    // A server whose loop runs late sends 2 ticks per snapshot sometimes and 4 at other times
    // (interval 2 with a 2-tick advance plus an occasional skipped advance); still smooth.
    [Fact]
    public void IrregularTwoAndFourTickSpacing_NeverRendersBackwards()
    {
        Run run = Simulate(intervalTicks: 2, framesPerSecond: 144, jitterMilliseconds: 10, seed: 11, irregular: true);
        output.WriteLine(run.ToString());

        Assert.Equal(0, run.BackwardsRenderTicks);
        Assert.Equal(0, run.BackwardsPositions);
        Assert.True(run.MaxErrorMetres < 0.05f, run.ToString());
    }

    private static Run Simulate(int intervalTicks, int framesPerSecond, int jitterMilliseconds, int seed, bool irregular = false)
    {
        const double LatencySeconds = 0.030d;
        const double DurationSeconds = 8d;
        // Measured from 1 s after the first snapshot (tick 100, at 1.5625 s), once the clock settled.
        const double WarmUpSeconds = (100 * TickSeconds) + 1d;
        Random random = new(seed);

        // Snapshot ticks and their in-order (TCP) arrival times.
        List<(ulong Tick, double Arrival)> arrivals = [];
        double lastArrival = 0d;
        ulong tick = 100;
        while (tick * TickSeconds < DurationSeconds + 1d)
        {
            int step = irregular && random.Next(5) == 0 ? intervalTicks * 2 : intervalTicks;
            tick += (ulong)step;
            double arrival = (tick * TickSeconds) + LatencySeconds + (random.NextDouble() * jitterMilliseconds / 1000d);
            lastArrival = Math.Max(lastArrival, arrival);
            arrivals.Add((tick, lastArrival));
        }

        ServerTickClock clock = new();
        TickInterpolationBuffer buffer = new();
        double frameSeconds = 1d / framesPerSecond;
        int next = 0;
        double previousRenderTick = double.NegativeInfinity;
        float previousX = float.NegativeInfinity;
        int backwardsTicks = 0;
        int backwardsPositions = 0;
        int pastNewest = 0;
        float maxError = 0f;
        float maxStep = 0f;
        double delaySum = 0d;
        int measured = 0;
        for (double now = 100 * TickSeconds; now < DurationSeconds; now += frameSeconds)
        {
            clock.Advance(frameSeconds);
            while (next < arrivals.Count && arrivals[next].Arrival <= now)
            {
                ulong sampleTick = arrivals[next].Tick;
                clock.Observe(sampleTick);
                buffer.AddSample(sampleTick, Position(sampleTick), Vector3.UnitX);
                next++;
            }

            if (!clock.HasEstimate || !buffer.TrySample(clock.RenderTick, out InterpolatedActorPose pose))
            {
                continue;
            }

            double renderTick = clock.RenderTick;
            if (renderTick < previousRenderTick)
            {
                backwardsTicks++;
            }

            if (pose.Position.X < previousX - 1e-5f)
            {
                backwardsPositions++;
            }

            if (now >= WarmUpSeconds)
            {
                if (renderTick > buffer.NewestTick!.Value)
                {
                    pastNewest++;
                }

                maxError = Math.Max(maxError, MathF.Abs(pose.Position.X - (float)(renderTick * MetresPerTick)));
                maxStep = Math.Max(maxStep, pose.Position.X - previousX);
                delaySum += clock.EstimatedTick - renderTick;
                measured++;
            }

            previousRenderTick = renderTick;
            previousX = pose.Position.X;
        }

        return new Run(backwardsTicks, backwardsPositions, pastNewest, maxError, maxStep, measured == 0 ? 0d : delaySum / measured);
    }

    private static Vector3 Position(ulong tick) => new((float)(tick * MetresPerTick), 1f, 0f);

    private sealed record Run(
        int BackwardsRenderTicks,
        int BackwardsPositions,
        int FramesPastNewestSample,
        float MaxErrorMetres,
        float MaxStepMetres,
        double MeanDelayTicks)
    {
        public override string ToString() =>
            $"backwards ticks {BackwardsRenderTicks}, backwards positions {BackwardsPositions}, past newest {FramesPastNewestSample}, " +
            $"max error {MaxErrorMetres:F4} m, max step {MaxStepMetres:F4} m, mean delay {MeanDelayTicks:F2} ticks";
    }
}
