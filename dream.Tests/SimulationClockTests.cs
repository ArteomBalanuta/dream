using Xunit;

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
}
