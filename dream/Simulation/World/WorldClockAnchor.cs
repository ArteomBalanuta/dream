namespace Dream.Simulation.World;

// Protocol v5: WorldTicks was the world clock at ServerTick. Clients derive time from their tick estimate.
public readonly record struct WorldClockAnchor(ulong ServerTick, long WorldTicks, long TicksPerDay, long StartOffsetTicks)
{
    public double WorldTicksAt(double serverTick) => WorldTicks + (serverTick - ServerTick);

    public double TimeOfDay01At(double serverTick)
    {
        if (TicksPerDay <= 0)
        {
            return WorldClockSettings.StartTimeOfDay01;
        }

        double days = DaysAt(serverTick);
        return days - Math.Floor(days);
    }

    // Whole days since day 0, counted like WorldClock.DayNumber ((W + S) / T), so a new world is on day 0.
    public long DayNumberAt(double serverTick) =>
        TicksPerDay <= 0 ? 0L : (long)Math.Floor(DaysAt(serverTick));

    private double DaysAt(double serverTick) => (WorldTicksAt(serverTick) + StartOffsetTicks) / TicksPerDay;
}
