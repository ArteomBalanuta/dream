using Xunit;
using Dream.Client.Presentation;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Client.Presentation;

public class ServerTickClockTests
{
    private const double TickSeconds = 1d / SimulationClock.AuthoritativeTicksPerSecond;

    [Fact]
    public void Reset_StartsAtJoinTickWithDefaultDelay()
    {
        ServerTickClock clock = new();

        clock.Reset(120);

        Assert.True(clock.HasEstimate);
        Assert.Equal(120d, clock.EstimatedTick);
        Assert.Equal(ServerTickClock.DefaultInterpolationDelayTicks, clock.InterpolationDelayTicks, 6);
    }

    [Fact]
    public void Advance_UsesAuthoritativeTickRate()
    {
        ServerTickClock clock = new();
        clock.Reset(10);

        clock.Advance(0.5d);

        Assert.Equal(10d + (SimulationClock.AuthoritativeTicksPerSecond * 0.5d), clock.EstimatedTick, 6);
    }

    [Fact]
    public void Advance_BeforeAnyTickIsKnown_DoesNothing()
    {
        ServerTickClock clock = new();

        clock.Advance(1d);

        Assert.False(clock.HasEstimate);
        Assert.Equal(0d, clock.EstimatedTick);
    }

    [Fact]
    public void Observe_FirstObservationWithoutReset_SnapsToTick()
    {
        ServerTickClock clock = new();

        clock.Observe(500);

        Assert.True(clock.HasEstimate);
        Assert.Equal(500d, clock.EstimatedTick);
    }

    [Fact]
    public void Observe_ConvergesToServerTickUnderJitter()
    {
        ServerTickClock clock = new();
        clock.Reset(100);
        ulong serverTick = 108;
        int[] jitterPattern = [0, 1, -1, 1, 0, -1, 1, -1];

        for (int frame = 0; frame < 640; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
            long jitter = jitterPattern[frame % jitterPattern.Length];
            clock.Observe((ulong)((long)serverTick + jitter));
        }

        Assert.InRange(clock.EstimatedTick, serverTick - 1.5d, serverTick + 1.5d);
    }

    [Fact]
    public void Observe_SmallOffset_SlewsInsteadOfSnapping()
    {
        ServerTickClock clock = new();
        clock.Reset(100);

        clock.Observe(110);

        Assert.True(clock.EstimatedTick > 100d);
        Assert.True(clock.EstimatedTick < 110d);
    }

    [Fact]
    public void Observe_LargeForwardJump_Snaps()
    {
        ServerTickClock clock = new();
        clock.Reset(100);

        clock.Observe(100 + (ulong)ServerTickClock.SnapThresholdTicks + 50);

        Assert.Equal(166d, clock.EstimatedTick);
        Assert.Equal(166UL, clock.NewestObservedTick);
    }

    [Fact]
    public void Observe_ServerPausedWhileClockRanAhead_SnapsBackward()
    {
        ServerTickClock clock = new();
        clock.Reset(100);
        clock.Advance(1d);

        clock.Observe(110);

        Assert.Equal(110d, clock.EstimatedTick);
    }

    [Fact]
    public void Observe_OlderOrDuplicateTick_DoesNotMoveEstimate()
    {
        ServerTickClock clock = new();
        clock.Reset(100);
        clock.Observe(104);
        double estimate = clock.EstimatedTick;

        clock.Observe(104);
        clock.Observe(102);

        Assert.Equal(estimate, clock.EstimatedTick);
        Assert.Equal(104UL, clock.NewestObservedTick);
    }

    [Fact]
    public void Observe_TimelineRestartFarBehind_Snaps()
    {
        ServerTickClock clock = new();
        clock.Reset(5000);

        clock.Observe(3);

        Assert.Equal(3d, clock.EstimatedTick);
        Assert.Equal(3UL, clock.NewestObservedTick);
    }

    [Fact]
    public void InterpolationDelay_SteadySnapshots_ShrinksToMinimum()
    {
        ServerTickClock clock = new();
        clock.Reset(0);
        ulong serverTick = 0;

        for (int frame = 0; frame < 640; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
            clock.Observe(serverTick);
            Assert.InRange(
                clock.InterpolationDelayTicks,
                ServerTickClock.MinInterpolationDelayTicks,
                ServerTickClock.MaxInterpolationDelayTicks);
        }

        Assert.True(clock.InterpolationDelayTicks < ServerTickClock.DefaultInterpolationDelayTicks);
        Assert.Equal(ServerTickClock.MinInterpolationDelayTicks, clock.InterpolationDelayTicks, 6);
    }

    [Fact]
    public void InterpolationDelay_BurstyArrival_GrowsAndStaysClamped()
    {
        ServerTickClock clock = new();
        clock.Reset(0);
        ulong serverTick = 0;
        const int burstFrames = 16;

        for (int frame = 1; frame <= 640; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
            if (frame % burstFrames != 0)
            {
                continue;
            }

            // Every tick of the last burst window arrives in the same frame.
            for (ulong tick = serverTick - burstFrames + 1; tick <= serverTick; tick++)
            {
                clock.Observe(tick);
                Assert.InRange(
                    clock.InterpolationDelayTicks,
                    ServerTickClock.MinInterpolationDelayTicks,
                    ServerTickClock.MaxInterpolationDelayTicks);
            }
        }

        Assert.True(clock.InterpolationDelayTicks > ServerTickClock.DefaultInterpolationDelayTicks);
        Assert.Equal(ServerTickClock.MaxInterpolationDelayTicks, clock.InterpolationDelayTicks, 6);
    }
}
