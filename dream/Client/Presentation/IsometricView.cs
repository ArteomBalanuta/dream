using OpenTK.Mathematics;
using Dream.Simulation.Players;

namespace Dream.Client.Presentation;

// The fixed high-angle play camera: a perspective camera at a constant offset from a focus point
// (the player's eye), looking at it. No rotation and no zoom.
public static class IsometricView
{
    public const float PitchDegrees = 50f;
    public const float YawDegrees = 45f;
    public const float Distance = 18f;
    public const float FieldOfViewDegrees = 35f;
    public const float NearPlane = 1f;
    public const float FarPlane = Distance + 80f;

    // From the focus point towards the camera.
    public static Vector3 FocusToCamera { get; } = ComputeFocusToCamera();

    public static void ResolveInto(Vector3 focus, Camera target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Vector3 toCamera = FocusToCamera;
        target.Position = focus + toCamera;
        target.SetFacing(-toCamera);
    }

    public static Matrix4 CreateProjection(float aspectRatio)
    {
        return CreateProjection(aspectRatio, Distance);
    }

    // The projection for a camera this far from its target: only the far plane depends on the distance.
    public static Matrix4 CreateProjection(float aspectRatio, float distance)
    {
        return Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(FieldOfViewDegrees),
            aspectRatio,
            NearPlane,
            FarPlaneFor(distance));
    }

    // The far plane keeps the fixed view's relation (FarPlane - Distance) at any distance.
    public static float FarPlaneFor(float distance) => distance + (FarPlane - Distance);

    // From the focus point towards a camera at this yaw, pitch and distance. The default pose gives FocusToCamera.
    public static Vector3 FocusToCameraFor(float yawDegrees, float pitchDegrees, float distance)
    {
        float pitch = MathHelper.DegreesToRadians(pitchDegrees);
        float yaw = MathHelper.DegreesToRadians(yawDegrees);
        return new Vector3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Sin(yaw)) * distance;
    }

    private static Vector3 ComputeFocusToCamera() => FocusToCameraFor(YawDegrees, PitchDegrees, Distance);
}

// Follows the eye with X and Z unchanged and Y smoothed, so jumps do not bob the camera.
public sealed class IsometricFocus
{
    public const float YSmoothingSeconds = 0.18f;
    public const float YSnapDistance = 4f;

    private float _y;
    private bool _initialised;

    public Vector3 Current { get; private set; }

    public Vector3 Advance(Vector3 eye, float deltaSeconds)
    {
        if (!_initialised || MathF.Abs(eye.Y - _y) > YSnapDistance)
        {
            _y = eye.Y;
            _initialised = true;
        }
        else if (deltaSeconds > 0f)
        {
            float blend = 1f - MathF.Exp(-deltaSeconds / YSmoothingSeconds);
            _y += (eye.Y - _y) * blend;
        }

        Current = new Vector3(eye.X, _y, eye.Z);
        return Current;
    }
}
