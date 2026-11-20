using OpenTK.Mathematics;

namespace Dream.Client.CameraControl;

// Zoom-to-cursor: the view target that keeps a world anchor under the same pixel when the distance changes
// and the orientation does not. See the tactical camera plan, "Zoom and zoom-to-cursor".
public static class ZoomAnchorSolver
{
    // false (target = default) when the ray back to the camera is too flat (r.y < 1e-3) or inputs are not finite.
    public static bool TrySolveTarget(
        Vector3 anchor,
        Vector3 previousCameraPosition,
        float yawDegrees,
        float pitchDegrees,
        float distance,
        float targetY,
        out Vector3 target) =>
        throw new NotImplementedException("tactical-camera stub");
}
