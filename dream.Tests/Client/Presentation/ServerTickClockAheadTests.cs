using Xunit;
using Dream.Client.Presentation;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Client.Presentation;

// When a late snapshot widens the delay, RenderTick ends up ahead of its target. It must slow
// down rather than freeze, so remote players keep moving while the gap closes.
public class ServerTickClockAheadTests
{
    private const double TickSeconds = 1d / SimulationClock.AuthoritativeTicksPerSecond;
    private const double FrameSeconds = 1d / 60d;

    [Fact]
    public void RenderTickAheadByLessThanFourTicks_StillAdvancesAndGapShrinks()
    {
        ServerTickClock clock = CreateClockAheadOfTarget();
        int aheadFrames = 0;

        for (int frame = 0; frame < 120; frame++)
        {
            double gapBefore = clock.RenderTick - Target(clock);
            double renderBefore = clock.RenderTick;
            clock.Advance(FrameSeconds);
            if (gapBefore <= 0d)
            {
                continue;
            }

            Assert.True(clock.RenderTick >= renderBefore, "render tick moved backwards");
            if (gapBefore < 4d)
            {
                aheadFrames++;
                Assert.True(
                    clock.RenderTick > renderBefore,
                    $"render tick froze at {renderBefore} while {gapBefore:F3} ticks ahead of the target");
                double gapAfter = clock.RenderTick - Target(clock);
                Assert.True(gapAfter < gapBefore, $"gap did not shrink: {gapBefore:F3} -> {gapAfter:F3}");
            }
        }

        Assert.True(aheadFrames > 0, "scenario never put the render tick less than 4 ticks ahead");
        Assert.True(clock.RenderTick <= Target(clock) + 1e-9d);
    }

    [Fact]
    public void HitchFrameWhileAhead_DoesNotFreezeOnceTargetPassesRenderTick()
    {
        ServerTickClock clock = CreateClockAheadOfTarget();
        double hitchTicks = 0.1d * SimulationClock.AuthoritativeTicksPerSecond;

        // Run normal frames until the render tick is far enough ahead that the pre-advance rate
        // clamps to 0, but a 100 ms hitch would still carry the target past it.
        bool reachedWindow = false;
        for (int frame = 0; frame < 120; frame++)
        {
            double gap = clock.RenderTick - Target(clock);
            if (gap >= 4d && gap < hitchTicks - 0.5d)
            {
                reachedWindow = true;
                break;
            }

            clock.Advance(FrameSeconds);
        }

        Assert.True(reachedWindow, "scenario never reached the hitch window");

        double renderBefore = clock.RenderTick;
        clock.Advance(0.1d);

        Assert.True(Target(clock) > renderBefore, "target should pass the render tick during the hitch");
        Assert.True(clock.RenderTick > renderBefore, $"render tick froze at {renderBefore} after the hitch");
        Assert.True(clock.RenderTick <= Target(clock) + 1e-9d, "render tick overshot the target");
    }

    private static double Target(ServerTickClock clock) => clock.EstimatedTick - clock.InterpolationDelayTicks;

    // Steady stream, then a 15-tick delivery stall and a late snapshot that widens the delay,
    // leaving RenderTick well ahead of EstimatedTick - InterpolationDelayTicks.
    private static ServerTickClock CreateClockAheadOfTarget()
    {
        ServerTickClock clock = new();
        clock.Reset(1000);
        ulong serverTick = 1000;
        for (int frame = 0; frame < 256; frame++)
        {
            serverTick++;
            clock.Advance(TickSeconds);
            clock.Observe(serverTick);
        }

        ulong lastObserved = serverTick;
        for (int frame = 0; frame <= 15; frame++)
        {
            clock.Advance(TickSeconds);
        }

        clock.Observe(lastObserved + 1);
        double gap = clock.RenderTick - Target(clock);
        Assert.True(gap > 6.5d, $"expected a wide gap, got {gap:F3}");
        return clock;
    }
}
