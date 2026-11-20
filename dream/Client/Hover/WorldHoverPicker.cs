using Dream.Rendering.Ui;

namespace Dream.Client.Hover;

// One full pick: asks every source for its best hit and resolves them (nearest depth, then kind priority
// within HoverPriority.DepthTieMetres, then lower id). Allocation free.
public sealed class WorldHoverPicker
{
    private readonly IHoverSource[] _sources;
    private readonly HoverHit[] _hits;

    public WorldHoverPicker(IReadOnlyList<IHoverSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = new IHoverSource[sources.Count];
        _hits = new HoverHit[sources.Count];
        for (int i = 0; i < _sources.Length; i++)
        {
            _sources[i] = sources[i];
        }
    }

    public bool TryPick(in HoverRay ray, HoverTargetId sticky, out HoverHit best)
    {
        // Collect every hit in front of the wall first: the tie window is measured from the nearest depth, so
        // the answer must not depend on source order.
        int count = 0;
        float min = float.PositiveInfinity;
        for (int i = 0; i < _sources.Length; i++)
        {
            if (!_sources[i].TryHit(in ray, sticky, HoverTuning.StickyInflate, out HoverHit hit) || hit.Depth > ray.WallDepth)
            {
                continue;
            }

            _hits[count++] = hit;
            if (hit.Depth < min)
            {
                min = hit.Depth;
            }
        }

        best = default;
        bool found = false;
        float limit = min + HoverPriority.DepthTieMetres;
        for (int i = 0; i < count; i++)
        {
            ref readonly HoverHit h = ref _hits[i];
            if (h.Depth > limit)
            {
                continue;
            }

            if (!found || Beats(in h, in best))
            {
                best = h;
                found = true;
            }
        }

        return found;
    }

    // Among tie candidates: the lower kind priority, then the lower id.
    private static bool Beats(in HoverHit a, in HoverHit b)
    {
        int pa = HoverPriority.Of(a.Target.Kind);
        int pb = HoverPriority.Of(b.Target.Kind);
        return pa != pb ? pa < pb : a.Target.Id < b.Target.Id;
    }
}
