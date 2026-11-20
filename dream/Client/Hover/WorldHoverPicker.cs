using Dream.Rendering.Ui;

namespace Dream.Client.Hover;

// One full pick: asks every source for its best hit and resolves them (nearest depth, then kind priority
// within HoverPriority.DepthTieMetres, then lower id). Allocation free.
public sealed class WorldHoverPicker
{
    private readonly IHoverSource[] _sources;

    public WorldHoverPicker(IReadOnlyList<IHoverSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = new IHoverSource[sources.Count];
        for (int i = 0; i < _sources.Length; i++)
        {
            _sources[i] = sources[i];
        }
    }

    public bool TryPick(in HoverRay ray, HoverTargetId sticky, out HoverHit best)
    {
        best = default;
        bool found = false;
        for (int i = 0; i < _sources.Length; i++)
        {
            if (!_sources[i].TryHit(in ray, sticky, HoverTuning.StickyInflate, out HoverHit hit) || hit.Depth > ray.WallDepth)
            {
                continue;
            }

            if (!found || IsBetter(in hit, in best))
            {
                best = hit;
                found = true;
            }
        }

        return found;
    }

    // Whether a beats b: the nearer one, unless they are within the tie distance, then the lower kind
    // priority, then the lower id.
    public static bool IsBetter(in HoverHit a, in HoverHit b)
    {
        float gap = a.Depth - b.Depth;
        if (MathF.Abs(gap) > HoverPriority.DepthTieMetres)
        {
            return gap < 0f;
        }

        int pa = HoverPriority.Of(a.Target.Kind);
        int pb = HoverPriority.Of(b.Target.Kind);
        return pa != pb ? pa < pb : a.Target.Id < b.Target.Id;
    }
}
