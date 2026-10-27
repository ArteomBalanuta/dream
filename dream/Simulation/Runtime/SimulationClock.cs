namespace Dream.Simulation.Runtime;

public sealed class SimulationClock
{
    public const int AuthoritativeTicksPerSecond = 64;
    public const float FixedDeltaSeconds = 1f / AuthoritativeTicksPerSecond;

    // After a stall, running every missed tick back to back teleports players and cubes and can
    // feed a spiral of ever-longer frames. Time beyond this many ticks is dropped instead.
    public const int DefaultMaxTicksPerAdvance = 8;

    private readonly int _maxTicksPerAdvance;
    private double _accumulatorSeconds;

    public SimulationClock(int maxTicksPerAdvance = DefaultMaxTicksPerAdvance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicksPerAdvance, 1);
        _maxTicksPerAdvance = maxTicksPerAdvance;
    }

    public double RemainingSeconds => _accumulatorSeconds;

    public long DroppedTicks { get; private set; }

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

        if (ticks > _maxTicksPerAdvance)
        {
            DroppedTicks += ticks - _maxTicksPerAdvance;
            ticks = _maxTicksPerAdvance;
        }

        return ticks;
    }
}
