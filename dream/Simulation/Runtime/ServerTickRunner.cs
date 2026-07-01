namespace Dream.Simulation.Runtime;

public sealed class ServerTickRunner
{
    private readonly SimulationClock _clock = new();

    public ulong LastCompletedTick { get; private set; }

    public double RemainingSeconds => _clock.RemainingSeconds;

    public int Advance(double elapsedSeconds, Action<ulong, float> tickAction)
    {
        ArgumentNullException.ThrowIfNull(tickAction);

        int executedTicks = _clock.QueueElapsed(elapsedSeconds);
        for (int i = 0; i < executedTicks; i++)
        {
            ulong tick = LastCompletedTick + 1;
            tickAction(tick, SimulationClock.FixedDeltaSeconds);
            LastCompletedTick = tick;
        }

        return executedTicks;
    }
}
