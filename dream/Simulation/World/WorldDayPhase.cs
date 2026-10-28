namespace Dream.Simulation.World;

// The coarse part of the day the world clock is in. Night wraps midnight: [20:00, 04:00).
public enum WorldDayPhase : byte
{
    Night,
    Dawn,
    Day,
    Dusk
}

public static class WorldDayPhases
{
    public const double DawnStart01 = 4d / 24d;
    public const double DayStart01 = 7d / 24d;
    public const double DuskStart01 = 17d / 24d;
    public const double NightStart01 = 20d / 24d;

    // timeOfDay01 is in [0, 1). Each bound is inclusive at its start, so the tick that lands
    // exactly on 04:00 is already dawn.
    public static WorldDayPhase At(double timeOfDay01)
    {
        if (timeOfDay01 < DawnStart01 || timeOfDay01 >= NightStart01)
        {
            return WorldDayPhase.Night;
        }

        if (timeOfDay01 < DayStart01)
        {
            return WorldDayPhase.Dawn;
        }

        return timeOfDay01 < DuskStart01 ? WorldDayPhase.Day : WorldDayPhase.Dusk;
    }
}
