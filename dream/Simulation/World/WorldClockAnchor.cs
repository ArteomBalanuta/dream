namespace Dream.Simulation.World;

// Protocol v5: WorldTicks was the world clock at ServerTick. Clients derive time from their tick estimate.
public readonly record struct WorldClockAnchor(ulong ServerTick, long WorldTicks, long TicksPerDay, long StartOffsetTicks)
{
    public double WorldTicksAt(double serverTick) => WorldTicks + (serverTick - ServerTick);

    // The time of day in [0, 1). Computed as a remainder in ticks divided by the day length, the
    // same operations as WorldClock.TimeOfDay01, so at a whole tick it is bit-identical to the
    // server's value and WorldDayPhases.At agrees with the server on the exact bound tick
    // (a fraction-of-days form, days - Floor(days), can land one ulp below a bound).
    public double TimeOfDay01At(double serverTick)
    {
        if (TicksPerDay <= 0)
        {
            return WorldClockSettings.StartTimeOfDay01;
        }

        double offsetTicks = OffsetTicksAt(serverTick);
        double ticksIntoDay = offsetTicks - Math.Floor(offsetTicks / TicksPerDay) * TicksPerDay;
        double time = ticksIntoDay / TicksPerDay;
        return time is >= 0d and < 1d ? time : 0d;
    }

    // Whole days since day 0, counted like WorldClock.DayNumber ((W + S) / T), so a new world is on day 0.
    public long DayNumberAt(double serverTick) =>
        TicksPerDay <= 0 ? 0L : (long)Math.Floor(OffsetTicksAt(serverTick) / TicksPerDay);

    private double OffsetTicksAt(double serverTick) => WorldTicksAt(serverTick) + StartOffsetTicks;
}
