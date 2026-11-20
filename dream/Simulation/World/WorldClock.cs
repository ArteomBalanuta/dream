namespace Dream.Simulation.World;

// The authoritative world clock. WorldTicks counts simulation ticks since the world was created
// and survives restarts (world_metadata.world_time_ticks); it never reads the wall clock, so the
// time of day only moves while the server ticks.
//
// Since wave T2 it is a gameplay input: the wolf population step (WorldSessionRuntime.Enemies)
// keeps a second wolf at night. Phase is a pure function of the persisted world ticks,
// TicksPerDay, the number of ticks run and any jumps (JumpForward), so those are lockstep inputs:
// servers that must stay in lockstep need the same values. Advance runs first in AdvanceAuthoritativeTick, before
// any gameplay. The clock itself stays out of SimulationStateChecksum and WorldStateChecksum; what
// it changes (the extra wolf's actor and id) is hashed there like any other enemy.
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

    // Moves the clock forward by ticks at once (the /dev tod command). Raises PhaseChanged once, with the
    // phase before and after, when the jump lands in another phase; the phases skipped over get no event.
    // Allocation-free.
    public void JumpForward(long ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        if (ticks == 0)
        {
            return;
        }

        WorldTicks += ticks;
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
