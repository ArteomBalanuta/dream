using Xunit;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Simulation.Runtime;


public class SimulationClockTests
{
    [Fact]
    public void QueueElapsed_AccumulatesFixedTicksAt64Hz()
    {
        SimulationClock clock = new();

        int first = clock.QueueElapsed(SimulationClock.FixedDeltaSeconds * 2.5d);

        Assert.Equal(2, first);
        Assert.InRange(clock.RemainingSeconds, 0.007d, 0.009d);
    }

    [Fact]
    public void QueueElapsed_AfterStall_CapsTicksAndDropsExcessTime()
    {
        // A 0.6 s stall used to replay ~38 ticks back to back and teleport players and cubes.
        SimulationClock clock = new();

        int ticks = clock.QueueElapsed(0.6d);

        Assert.Equal(SimulationClock.DefaultMaxTicksPerAdvance, ticks);
        Assert.Equal(38 - SimulationClock.DefaultMaxTicksPerAdvance, clock.DroppedTicks);
        Assert.InRange(clock.RemainingSeconds, 0d, SimulationClock.FixedDeltaSeconds);
        Assert.Equal(1, clock.QueueElapsed(SimulationClock.FixedDeltaSeconds));
    }

    [Fact]
    public void QueueElapsed_WithinCap_DropsNothing()
    {
        SimulationClock clock = new(maxTicksPerAdvance: 4);

        Assert.Equal(4, clock.QueueElapsed(SimulationClock.FixedDeltaSeconds * 4.5d));
        Assert.Equal(0, clock.DroppedTicks);
        Assert.Equal(4, clock.QueueElapsed(SimulationClock.FixedDeltaSeconds * 9d));
        Assert.Equal(5, clock.DroppedTicks);
    }
}

public class ServerTickRunnerTests
{
    [Fact]
    public void Advance_ExecutesTicksInSequence()
    {
        ServerTickRunner runner = new();
        List<ulong> executed = [];

        int count = runner.Advance(SimulationClock.FixedDeltaSeconds * 3.2d, (tick, fixedDeltaSeconds) =>
        {
            executed.Add(tick);
            Assert.Equal(SimulationClock.FixedDeltaSeconds, fixedDeltaSeconds, 6);
        });

        Assert.Equal(3, count);
        Assert.Equal([1ul, 2ul, 3ul], executed);
        Assert.Equal(3ul, runner.LastCompletedTick);
    }

    [Fact]
    public void Advance_RetainsRemainingPartialFrameTimeForPresentationSmoothing()
    {
        ServerTickRunner runner = new();

        runner.Advance(SimulationClock.FixedDeltaSeconds * 1.5d, (_, _) => { });

        Assert.InRange(runner.RemainingSeconds, 0.007d, 0.009d);
    }

    [Fact]
    public void Advance_AfterStall_RunsAtMostTheCapAndKeepsTickNumbersContiguous()
    {
        ServerTickRunner runner = new(maxTicksPerAdvance: 3);
        List<ulong> executed = [];

        int count = runner.Advance(1d, (tick, _) => executed.Add(tick));

        Assert.Equal(3, count);
        Assert.Equal([1ul, 2ul, 3ul], executed);
        Assert.Equal(61, runner.DroppedTicks);
    }
}
