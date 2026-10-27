using Xunit;
using Dream.Client.Presentation;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Client.Presentation;

// A late snapshot used to pull the estimate back and widen the delay in the same frame, so
// remote players rendered at EstimatedTick - InterpolationDelayTicks jumped backwards.
public class ServerTickClockMonotonicTests
{
    private const double TickSeconds = 1d / SimulationClock.AuthoritativeTicksPerSecond;
    private const double FrameSeconds = 1d / 60d;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(15)]
    public void LateSnapshotAfterSteadyStream_NeverDecreasesRenderTick(int lateness)
    {
        ServerTickClock clock = new();
        clock.Reset(1000);
        ulong serverTick = 1000;
        double previous = clock.RenderTick;

        for (int frame = 0; frame < 256; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
            AssertNotBackwards(ref previous, clock.RenderTick);
            clock.Observe(serverTick);
            AssertNotBackwards(ref previous, clock.RenderTick);
        }

        // Delivery stalls while the client keeps advancing, then the next snapshot arrives
        // `lateness` ticks behind the estimate.
        ulong lastObserved = serverTick;
        for (int frame = 0; frame <= lateness; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
            AssertNotBackwards(ref previous, clock.RenderTick);
        }

        clock.Observe(lastObserved + 1);
        Assert.Equal(lastObserved + 1, clock.NewestObservedTick);
        AssertNotBackwards(ref previous, clock.RenderTick);

        for (int frame = 0; frame < 64; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
            AssertNotBackwards(ref previous, clock.RenderTick);
            clock.Observe(serverTick);
            AssertNotBackwards(ref previous, clock.RenderTick);
        }
    }

    [Fact]
    public void JitterSpikeRaisingDelay_DoesNotStepRenderTickBack()
    {
        ServerTickClock clock = new();
        clock.Reset(500);
        ulong serverTick = 500;
        for (int frame = 0; frame < 256; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
            clock.Observe(serverTick);
        }

        double delayBefore = clock.InterpolationDelayTicks;
        double previous = clock.RenderTick;
        // Bursty delivery: alternating early and late snapshots on a 60 Hz render loop.
        int[] burst = [6, -6, 7, -7, 8, -8];
        foreach (int offset in burst)
        {
            serverTick += 2;
            clock.Advance(FrameSeconds);
            AssertNotBackwards(ref previous, clock.RenderTick);
            clock.Observe((ulong)((long)serverTick + offset));
            AssertNotBackwards(ref previous, clock.RenderTick);
        }

        Assert.True(clock.InterpolationDelayTicks > delayBefore);
    }

    [Fact]
    public void ForwardSnap_RenderTickFollows()
    {
        ServerTickClock clock = new();
        clock.Reset(100);
        clock.Advance(TickSeconds);

        clock.Observe(400);

        Assert.Equal(400d, clock.EstimatedTick);
        Assert.Equal(400d - clock.InterpolationDelayTicks, clock.RenderTick, 6);
    }

    [Fact]
    public void Reset_RenderTickFollowsEvenBackwards()
    {
        ServerTickClock clock = new();
        clock.Reset(5000);
        clock.Advance(1d);

        clock.Reset(20);

        Assert.Equal(20d - clock.InterpolationDelayTicks, clock.RenderTick, 6);
    }

    [Fact]
    public void LongStall_RenderTickJumpsForwardToTarget()
    {
        ServerTickClock clock = new();
        clock.Reset(100);

        clock.Advance(2d);

        Assert.Equal(clock.EstimatedTick - clock.InterpolationDelayTicks, clock.RenderTick, 6);
    }

    [Fact]
    public void RenderTick_ConvergesToEstimateMinusDelayAfterLateSnapshot()
    {
        ServerTickClock clock = new();
        clock.Reset(1000);
        ulong serverTick = 1000;
        for (int frame = 0; frame < 128; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
            clock.Observe(serverTick);
        }

        ulong lastObserved = serverTick;
        for (int frame = 0; frame <= 12; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
        }

        clock.Observe(lastObserved + 1);
        Assert.True(clock.RenderTick > clock.EstimatedTick - clock.InterpolationDelayTicks);

        for (int frame = 0; frame < 256; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
            clock.Observe(serverTick);
        }

        Assert.InRange(
            clock.RenderTick,
            clock.EstimatedTick - clock.InterpolationDelayTicks - 0.05d,
            clock.EstimatedTick - clock.InterpolationDelayTicks + 0.05d);
    }

    [Fact]
    public void RenderTick_CatchesUpWhenTargetMovesAhead()
    {
        ServerTickClock clock = new();
        clock.Reset(1000);
        ulong serverTick = 1000;

        // Early snapshots pull the estimate forward faster than real time.
        for (int frame = 0; frame < 64; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
            clock.Observe(serverTick + 10);
        }

        for (int frame = 0; frame < 256; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
            clock.Observe(serverTick + 10);
        }

        Assert.InRange(
            clock.RenderTick,
            clock.EstimatedTick - clock.InterpolationDelayTicks - 0.05d,
            clock.EstimatedTick - clock.InterpolationDelayTicks + 1e-9d);
    }

    private static void AssertNotBackwards(ref double previous, double current)
    {
        Assert.True(current >= previous, $"render tick moved back from {previous} to {current}");
        previous = current;
    }
}
