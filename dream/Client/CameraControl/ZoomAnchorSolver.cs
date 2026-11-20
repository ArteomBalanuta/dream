using Dream.Client.Presentation;
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
        out Vector3 target)
    {
        target = default;
        if (!IsFinite(anchor) || !IsFinite(previousCameraPosition)
            || !float.IsFinite(yawDegrees) || !float.IsFinite(pitchDegrees)
            || !float.IsFinite(distance) || !float.IsFinite(targetY))
        {
            return false;
        }

        Vector3 toCamera = previousCameraPosition - anchor;
        if (toCamera.LengthSquared < 1e-8f)
        {
            return false;
        }

        Vector3 r = toCamera.Normalized();
        if (r.Y < 1e-3f)
        {
            return false;
        }

        // u: unit vector target -> camera at the new orientation. The new camera sits at T + u*d on the ray A + r*t.
        Vector3 u = IsometricView.FocusToCameraFor(yawDegrees, pitchDegrees, 1f);
        float t = (targetY + u.Y * distance - anchor.Y) / r.Y;
        Vector3 solved = new(
            anchor.X + r.X * t - u.X * distance,
            targetY,
            anchor.Z + r.Z * t - u.Z * distance);
        if (!IsFinite(solved))
        {
            return false;
        }

        target = solved;
        return true;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
