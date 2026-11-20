using Dream.Rendering.Ui;

namespace Dream.Client.Hover;

// Owns the one HoverInfo the HUD draws and the per-kind providers that fill it.
public sealed class HoverInfoComposer
{
    public HoverInfoComposer(IReadOnlyList<IHoverInfoProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        throw new NotImplementedException("world-hover stub");
    }

    public HoverInfo Info => throw new NotImplementedException("world-hover stub");

    // Refills Info for target from live state; true when it did. When target is gone (died, despawned) Info
    // keeps its last values with Defeated set and the health bar at 0, and this returns false. None clears Info.
    public bool Compose(HoverTargetId target) => throw new NotImplementedException("world-hover stub");
}
