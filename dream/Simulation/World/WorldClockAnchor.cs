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

        double days = (WorldTicksAt(serverTick) + StartOffsetTicks) / TicksPerDay;
        return days - Math.Floor(days);
    }
}
