namespace Dream.Simulation.Runtime;

public sealed class SimulationClock
{
    public const int AuthoritativeTicksPerSecond = 64;
    public const float FixedDeltaSeconds = 1f / AuthoritativeTicksPerSecond;

    private double _accumulatorSeconds;

    public double RemainingSeconds => _accumulatorSeconds;

    public int QueueElapsed(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0d)
        {
            return 0;
        }

        _accumulatorSeconds += elapsedSeconds;
        int ticks = 0;
        while (_accumulatorSeconds + 1e-9d >= FixedDeltaSeconds)
        {
            _accumulatorSeconds -= FixedDeltaSeconds;
            ticks++;
        }

        return ticks;
    }
}
