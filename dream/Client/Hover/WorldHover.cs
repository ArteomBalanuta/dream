using Dream.Client.Input;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Client.Hover;

// The world hover pipeline for one scene: label proxy, throttled ray pick across the sources, resolution
// (depth, then kind priority within HoverPriority.DepthTieMetres, then id), stickiness, and the lifecycle
// (enter/stay/exit, panel dwell, warm swap, linger, defeated hold). Update is called once per frame and
// must not allocate.
public sealed class WorldHover
{
    private const float TimerEpsilon = 1e-6f;

    private readonly IHoverSource?[] _byKind;
    private readonly WorldHoverPicker _picker;
    private readonly HoverTracker _tracker = new();

    private bool _havePick;
    private HoverHit _pick;
    private bool _pickHit;
    private Vector2 _pickPixel;
    private Matrix4 _pickView;
    private Matrix4 _pickProjection;
    private float _sincePick;

    public WorldHover(IReadOnlyList<IHoverSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _picker = new WorldHoverPicker(sources);
        int size = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            size = Math.Max(size, (int)sources[i].Kind + 1);
        }

        _byKind = new IHoverSource?[size];
        for (int i = 0; i < sources.Count; i++)
        {
            _byKind[(int)sources[i].Kind] ??= sources[i];
        }
    }

    // The last frame Update produced (default before the first).
    public HoverFrame Frame => _tracker.Frame;

    public HoverFrame Update(in HoverUpdate input)
    {
        float dt = float.IsFinite(input.Dt) ? MathF.Max(0f, input.Dt) : 0f;
        HoverTargetId previous = _tracker.Frame.Current;
        HoverTargetId current = HoverTargetId.None;
        Vector3 anchor = default;
        HoverOrigin origin = HoverOrigin.None;

        if (input.Gated)
        {
            _havePick = false;
            return _tracker.Advance(current, anchor, origin, dt, false, true);
        }

        if (!input.LabelTarget.IsNone && TryGetLive(input.LabelTarget, out Vector3 labelAnchor))
        {
            current = input.LabelTarget;
            anchor = labelAnchor;
            origin = HoverOrigin.Label;
            _havePick = false;
        }
        else if (input.OverUi)
        {
            _havePick = false;
        }
        else if (ResolveRay(in input, dt, previous, out current, out anchor))
        {
            origin = HoverOrigin.Ray;
        }

        HoverTargetId panelTarget = _tracker.PanelTarget;
        bool panelGone = !panelTarget.IsNone && panelTarget != current && !TryGetLive(panelTarget, out _);
        return _tracker.Advance(current, anchor, origin, dt, panelGone, false);
    }

    // Drops the hover and the panel at once, with no linger (a scene change, a world transfer).
    public void Clear()
    {
        _tracker.Clear();
        _havePick = false;
        _pickHit = false;
        _sincePick = 0f;
    }

    private bool ResolveRay(in HoverUpdate input, float dt, HoverTargetId sticky, out HoverTargetId target, out Vector3 anchor)
    {
        target = HoverTargetId.None;
        anchor = default;
        if (input.World is null || !input.HaveCamera)
        {
            _havePick = false;
            return false;
        }

        _sincePick += dt;
        Vector2 moved = input.Pixel - _pickPixel;
        bool timerDue = _sincePick >= HoverTuning.RefreshSeconds - TimerEpsilon;
        bool otherDue = !_havePick
            || input.ForcePick
            || !(moved.LengthSquared <= HoverTuning.RepickPixels * HoverTuning.RepickPixels)
            || input.View != _pickView
            || input.Projection != _pickProjection;

        if (timerDue || otherDue)
        {
            _sincePick = timerDue && !otherDue ? MathF.Max(0f, _sincePick - HoverTuning.RefreshSeconds) : 0f;
            _havePick = true;
            _pickPixel = input.Pixel;
            _pickView = input.View;
            _pickProjection = input.Projection;
            _pickHit = false;
            if (WorldCursorPicker.TryBuildRay(input.View, input.Projection, input.Pixel, input.Viewport, out Vector3 o, out Vector3 d))
            {
                float wall = WorldCursorPicker.WallDepth(input.World, o, d);
                HoverRay ray = new(o, d, wall);
                _pickHit = _picker.TryPick(in ray, sticky, out _pick);
            }
        }

        if (!_pickHit)
        {
            return false;
        }

        // Reused or fresh, the answer is re-validated now: a thing that went away is gone, and a moving
        // thing's anchor is current.
        if (!TryGetLive(_pick.Target, out anchor))
        {
            return false;
        }

        target = _pick.Target;
        return true;
    }

    private bool TryGetLive(HoverTargetId id, out Vector3 anchor)
    {
        int kind = (int)id.Kind;
        if (id.IsNone || (uint)kind >= (uint)_byKind.Length || _byKind[kind] is not { } source)
        {
            anchor = default;
            return false;
        }

        return source.TryGetLive(id, out anchor);
    }
}
