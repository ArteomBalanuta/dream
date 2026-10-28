using Dream.Simulation.World;

namespace Dream.Client.Presentation;

// The replicated world clock on the client. The renderer reads TimeOfDay01(renderTick).
public sealed class ClientWorldClock
{
    private WorldClockAnchor _anchor;

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

    public void Clear()
    {
        _anchor = default;
        HasAnchor = false;
    }

    public float TimeOfDay01(double serverTick) =>
        HasAnchor ? (float)_anchor.TimeOfDay01At(serverTick) : (float)WorldClockSettings.StartTimeOfDay01;

    public long DayNumber(double serverTick) => HasAnchor ? _anchor.DayNumberAt(serverTick) : 0L;
}
