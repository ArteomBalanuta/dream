using Dream.Simulation.World;

namespace Dream.Client.Presentation;

// The replicated world clock on the client. The renderer reads TimeOfDay01(renderTick).
public sealed class ClientWorldClock
{
    private WorldClockAnchor _anchor;

    public bool HasAnchor { get; private set; }

    public WorldClockAnchor Anchor => _anchor;

    public void Apply(WorldClockAnchor anchor)
    {
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
}
