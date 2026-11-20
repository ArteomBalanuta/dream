using Dream.Simulation.World;

namespace Dream.Client.Presentation;

// The replicated world clock on the client. The renderer reads TimeOfDay01(renderTick).
public sealed class ClientWorldClock
{
    private WorldClockAnchor _anchor;

    // The last jump anchor. A chunked join is applied when its last chunk lands, so a jump can
    // overtake it; the join then must not roll the clock back to its older anchor.
    private WorldClockAnchor? _heldJump;

    public bool HasAnchor { get; private set; }

    public WorldClockAnchor Anchor => _anchor;

    // An anchor without a day length (TicksPerDay <= 0) is not a server clock: it clears the clock,
    // so the client shows the start time of day.
    public void Apply(WorldClockAnchor anchor)
    {
        if (anchor.TicksPerDay <= 0)
        {
            Clear();
            return;
        }

        _anchor = anchor;
        HasAnchor = true;
    }

    // The anchor of JoinAcceptedMessage (null when absent).
    public void ApplyJoin(WorldClockAnchor? anchor)
    {
        WorldClockAnchor? held = _heldJump;
        _heldJump = null;
        if (anchor is not { } j || j.TicksPerDay <= 0)
        {
            Clear();
            return;
        }

        if (held is { } h
            && h.TicksPerDay == j.TicksPerDay
            && h.StartOffsetTicks == j.StartOffsetTicks
            && h.ServerTick >= j.ServerTick
            && h.WorldTicks - (long)h.ServerTick > j.WorldTicks - (long)j.ServerTick)
        {
            Apply(h);
            return;
        }

        Apply(j);
    }

    // A server.world-clock message: a jump on the join's own timeline.
    public void ApplyJump(WorldClockAnchor anchor)
    {
        if (anchor.TicksPerDay <= 0)
        {
            return;
        }

        _heldJump = anchor;
        Apply(anchor);
    }

    public void Clear()
    {
        _anchor = default;
        HasAnchor = false;
    }

    // For rendering. The float can round across a phase bound on the bound tick itself, so phase
    // decisions use Phase (or TimeOfDay01Exact), never WorldDayPhases.At(TimeOfDay01(...)).
    public float TimeOfDay01(double serverTick) => (float)TimeOfDay01Exact(serverTick);

    // The anchor's double math: at a whole server tick it equals the server's WorldClock.TimeOfDay01.
    public double TimeOfDay01Exact(double serverTick) =>
        HasAnchor ? _anchor.TimeOfDay01At(serverTick) : WorldClockSettings.StartTimeOfDay01;

    // The day phase at serverTick; at a whole tick it matches the server's WorldClock.Phase.
    public WorldDayPhase Phase(double serverTick) => WorldDayPhases.At(TimeOfDay01Exact(serverTick));

    public long DayNumber(double serverTick) => HasAnchor ? _anchor.DayNumberAt(serverTick) : 0L;
}
