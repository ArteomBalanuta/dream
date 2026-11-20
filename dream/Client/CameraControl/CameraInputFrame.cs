using OpenTK.Mathematics;

namespace Dream.Client.CameraControl;

// Everything the camera reads from input for one rendered frame, accumulated since the last one.
// EdgePan / KeyPan: screen space, x right, y up (towards the top of the window), length <= 1, already gated.
// RotateDragPixels: horizontal pixels moved while a middle drag that started over the world is held (+ = right).
// RotateDragActive: such a drag is held now. RotateDragReleased: it ended since the last frame.
// RotateSteps: ',' = -1 and '.' = +1, summed over presses (never per held frame).
// ZoomNotches: wheel notches, + = zoom in, already gated by OverHud, with InvertZoom applied.
public readonly record struct CameraInputFrame(
    Vector2 EdgePan,
    Vector2 KeyPan,
    float RotateDragPixels,
    bool RotateDragActive,
    bool RotateDragReleased,
    int RotateSteps,
    float ZoomNotches,
    bool RecentrePressed,
    bool ToggleAnchorPressed)
{
    public static CameraInputFrame None { get; } = default;
}
