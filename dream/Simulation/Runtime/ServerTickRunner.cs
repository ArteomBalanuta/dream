namespace Dream.Simulation.Runtime;

public sealed class ServerTickRunner
{
    private readonly SimulationClock _clock;

    public ServerTickRunner(int maxTicksPerAdvance = SimulationClock.DefaultMaxTicksPerAdvance)
    {
        _clock = new SimulationClock(maxTicksPerAdvance);
    }

    public ulong LastCompletedTick { get; private set; }

    public double RemainingSeconds => _clock.RemainingSeconds;

    public long DroppedTicks => _clock.DroppedTicks;

    public int Advance(double elapsedSeconds, Action<ulong, float> tickAction)
    {
        ArgumentNullException.ThrowIfNull(tickAction);

        return Advance(
            elapsedSeconds,
            tickAction,
            static (action, tick, fixedDeltaSeconds) => action(tick, fixedDeltaSeconds));
    }

    // Callers pass a static lambda plus explicit state so a tick allocates no closure.
    public int Advance<TState>(double elapsedSeconds, TState state, Action<TState, ulong, float> tickAction)
    {
        ArgumentNullException.ThrowIfNull(tickAction);

        int executedTicks = _clock.QueueElapsed(elapsedSeconds);
        for (int i = 0; i < executedTicks; i++)
        {
            ulong tick = LastCompletedTick + 1;
            tickAction(state, tick, SimulationClock.FixedDeltaSeconds);
            LastCompletedTick = tick;
        }

        return executedTicks;
    }
}
