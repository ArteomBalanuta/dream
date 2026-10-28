namespace Dream.Simulation.World;

// The authoritative world clock. WorldTicks counts simulation ticks since the world was created
// and survives restarts (world_metadata.world_time_ticks); it never reads the wall clock, so the
// time of day only moves while the server ticks. It is presentation state: it stays out of
// SimulationStateChecksum and WorldStateChecksum, and gameplay does not branch on it.
public sealed class WorldClock
{
    public WorldClock(WorldClockSettings settings, long worldTicks = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.TicksPerDay, 1L);
        ArgumentOutOfRangeException.ThrowIfNegative(worldTicks);
        Settings = settings;
        WorldTicks = worldTicks;
        Phase = WorldDayPhases.At(TimeOfDay01);
    }

    public WorldClockSettings Settings { get; }

    public long WorldTicks { get; private set; }

    // Whole days since day 0, which starts at the world's 00:00 (a new world is on day 0 at 08:00).
    public long DayNumber => (WorldTicks + Settings.StartOffsetTicks) / Settings.TicksPerDay;

    public double TimeOfDay01 =>
        (WorldTicks + Settings.StartOffsetTicks) % Settings.TicksPerDay / (double)Settings.TicksPerDay;

    public WorldDayPhase Phase { get; private set; }

    // Raised on the tick thread with (previous, current) when Advance crosses a phase bound.
    public event Action<WorldDayPhase, WorldDayPhase>? PhaseChanged;

    // One authoritative tick. Allocation-free.
    public void Advance()
    {
        WorldTicks++;
        WorldDayPhase phase = WorldDayPhases.At(TimeOfDay01);
        if (phase == Phase)
        {
            return;
        }

        WorldDayPhase previous = Phase;
        Phase = phase;
        PhaseChanged?.Invoke(previous, phase);
    }

    public WorldClockAnchor CaptureAnchor(ulong serverTick) =>
        new(serverTick, WorldTicks, Settings.TicksPerDay, Settings.StartOffsetTicks);
}
