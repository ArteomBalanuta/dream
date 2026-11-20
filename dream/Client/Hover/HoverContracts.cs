using Dream.Client.Navigation;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Client.Hover;

// Which kind wins when two hits are within DepthTieMetres of each other along the ray: the lower value.
public static class HoverPriority
{
    public const float DepthTieMetres = 0.35f;

    public static int Of(HoverKind kind) => kind switch
    {
        HoverKind.Enemy => 0,
        HoverKind.RemotePlayer => 1,
        HoverKind.Npc => 2,
        HoverKind.Interactable => 3,
        HoverKind.GroundItem => 4,
        _ => int.MaxValue
    };
}

// The cursor ray, unit Direction, and the distance along it to the first blocked world cell (infinity when
// none): a thing behind that wall cannot be hovered.
public readonly record struct HoverRay(Vector3 Origin, Vector3 Direction, float WallDepth);

// One kind's best hit: Depth is the distance along the ray, Anchor the point aim and clicks use.
public readonly record struct HoverHit(HoverTargetId Target, float Depth, Vector3 Anchor);

// One per hoverable kind. Both calls must not allocate.
public interface IHoverSource
{
    HoverKind Kind { get; }

    // The nearest thing of this kind the ray hits with Depth <= ray.WallDepth. The sticky target (when it is of
    // this kind) is tested with its volume scaled by inflate, so a hover does not flicker at the edge.
    bool TryHit(in HoverRay ray, HoverTargetId sticky, float inflate, out HoverHit hit);

    // Whether id still exists and can be hovered (alive, still in the cache), and its anchor now.
    bool TryGetLive(HoverTargetId id, out Vector3 anchor);
}

// One per hoverable kind. Fills info for id from live client state each frame; false when id is gone.
// Must not allocate while the values it shows are unchanged.
public interface IHoverInfoProvider
{
    HoverKind Kind { get; }

    bool TryDescribe(HoverTargetId id, HoverInfo info);
}

public enum HoverPhase : byte
{
    None,
    Entered,
    Stay,
    Exited
}

public enum HoverOrigin : byte
{
    None,
    Ray,
    Label
}

// The one hover answer for a frame. Current is what the cursor, the click, the aim and the highlight use: it
// changes at once. PanelTarget/PanelAlpha are the info panel's debounced view of it (dwell, warm swap, linger,
// defeated hold). Phase is Current's transition this frame; Previous is Current's value last frame.
public readonly record struct HoverFrame(
    HoverTargetId Current,
    HoverTargetId Previous,
    HoverPhase Phase,
    Vector3 Anchor,
    HoverOrigin Origin,
    HoverTargetId PanelTarget,
    float PanelAlpha,
    float DwellSeconds)
{
    public bool PanelVisible => !PanelTarget.IsNone && PanelAlpha > 0f;
}

// Everything WorldHover needs for one frame.
// Gated: a gate is closed (loading, no gameplay input, unfocused, modal, camera or item drag) - clear at once.
// OverUi: the pointer is over the HUD and not on a world label - clear (the panel may linger).
// LabelTarget: the world label the HUD's pointer rested on last frame, or None; it beats the ray.
// World/HaveCamera/View/Projection/Pixel/Viewport: what the ray needs.
// ForcePick: a button went down this frame, so the pick must be fresh, not a throttled reuse.
public readonly record struct HoverUpdate(
    float Dt,
    bool Gated,
    bool OverUi,
    HoverTargetId LabelTarget,
    INavWorld? World,
    bool HaveCamera,
    Matrix4 View,
    Matrix4 Projection,
    Vector2 Pixel,
    Vector2i Viewport,
    bool ForcePick);
