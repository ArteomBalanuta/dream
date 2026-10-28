namespace Dream.Simulation.World;

public sealed class WorldClock
{
    public WorldClock(WorldClockSettings settings, long worldTicks = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.TicksPerDay, 1L);
        ArgumentOutOfRangeException.ThrowIfNegative(worldTicks);
        Settings = settings;
        WorldTicks = worldTicks;
    }

    public WorldClockSettings Settings { get; }

    public long WorldTicks { get; private set; }

    public WorldClockAnchor CaptureAnchor(ulong serverTick) =>
        new(serverTick, WorldTicks, Settings.TicksPerDay, Settings.StartOffsetTicks);
}
