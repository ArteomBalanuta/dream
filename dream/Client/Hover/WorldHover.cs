namespace Dream.Client.Hover;

// The world hover pipeline for one scene: label proxy, throttled ray pick across the sources, resolution
// (depth, then kind priority within HoverPriority.DepthTieMetres, then id), stickiness, and the lifecycle
// (enter/stay/exit, panel dwell, warm swap, linger, defeated hold). Update is called once per frame and
// must not allocate.
public sealed class WorldHover
{
    public WorldHover(IReadOnlyList<IHoverSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        throw new NotImplementedException("world-hover stub");
    }

    // The last frame Update produced (default before the first).
    public HoverFrame Frame => throw new NotImplementedException("world-hover stub");

    public HoverFrame Update(in HoverUpdate input) => throw new NotImplementedException("world-hover stub");

    // Drops the hover and the panel at once, with no linger (a scene change, a world transfer).
    public void Clear() => throw new NotImplementedException("world-hover stub");
}
