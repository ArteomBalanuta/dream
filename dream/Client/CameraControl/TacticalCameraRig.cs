using OpenTK.Mathematics;

namespace Dream.Client.CameraControl;

// The tactical camera's state: a goal (pan offset, yaw, distance) and the current pose that follows it.
// Advance once per rendered frame with the unscaled frame time.
public sealed class TacticalCameraRig
{
    public TacticalCameraRig(TacticalCameraSettings settings)
    {
        throw new NotImplementedException("tactical-camera stub");
    }

    public TacticalCameraSettings Settings
    {
        get => throw new NotImplementedException("tactical-camera stub");
        set => throw new NotImplementedException("tactical-camera stub");
    }

    // The current pose: what renders.
    public TacticalCameraPose Pose => throw new NotImplementedException("tactical-camera stub");

    public CameraAnchorMode AnchorMode => throw new NotImplementedException("tactical-camera stub");

    // Current view target minus the player focus, Y always 0.
    public Vector3 PanOffset => throw new NotImplementedException("tactical-camera stub");

    // True from a zoom gesture's first notch until the distance has settled on its goal.
    public bool ZoomGestureActive => throw new NotImplementedException("tactical-camera stub");

    // Called once when a gesture starts (ZoomNotches != 0 while !ZoomGestureActive), before Advance.
    // null: zoom about the view centre for this gesture.
    public void LatchZoomAnchor(Vector3? worldAnchor) => throw new NotImplementedException("tactical-camera stub");

    public TacticalCameraPose Advance(Vector3 playerFocus, in CameraInputFrame input, float realDeltaSeconds) =>
        throw new NotImplementedException("tactical-camera stub");

    // Sets goal and current together (no smoothing). A null argument keeps that value.
    public void SnapTo(Vector3 playerFocus, float? yawDegrees, float? distance, Vector2? panOffsetXz, CameraAnchorMode? mode) =>
        throw new NotImplementedException("tactical-camera stub");

    // Default pose on the player, zero offset, zero velocity, the settings' anchor mode.
    public void Reset(Vector3 playerFocus) => throw new NotImplementedException("tactical-camera stub");
}
