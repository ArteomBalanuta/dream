using Dream.Simulation.Runtime;

namespace Dream.Simulation.World;

// Per-world clock settings. TicksPerDay is not persisted (from DREAM_DAY_LENGTH_MINUTES each run);
// a new world (WorldTicks 0) shows StartTimeOfDay01 = 08:00.
public readonly record struct WorldClockSettings(long TicksPerDay, long StartOffsetTicks)
{
    public const int DefaultDayLengthMinutes = 30;
    public const double StartTimeOfDay01 = 8d / 24d;

    public static WorldClockSettings Default { get; } = FromDayLengthMinutes(DefaultDayLengthMinutes); // 115200, 38400

    public static WorldClockSettings FromDayLengthMinutes(double minutes)
    {
        long ticksPerDay = Math.Max(24L, (long)Math.Round(minutes * 60d * SimulationClock.AuthoritativeTicksPerSecond));
        return new WorldClockSettings(ticksPerDay, (long)Math.Round(ticksPerDay * StartTimeOfDay01));
    }
}
