using Dream.Client.Presentation;
using Dream.Simulation.Players;
using OpenTK.Mathematics;

namespace Dream.Client.CameraControl;

// One camera pose: a view target the camera orbits, the yaw and pitch of the target-to-camera
// direction, and the distance along it. Default(target) is the fixed isometric view.
public readonly record struct TacticalCameraPose(Vector3 Target, float YawDegrees, float PitchDegrees, float Distance)
{
    // From the target towards the camera, Distance long.
    public Vector3 FocusToCamera => IsometricView.FocusToCameraFor(YawDegrees, PitchDegrees, Distance);

    public Vector3 Position => Target + FocusToCamera;

    // Screen up projected on the ground (from the camera towards the target, flattened).
    public Vector3 GroundForward
    {
        get
        {
            Vector3 away = -IsometricView.FocusToCameraFor(YawDegrees, PitchDegrees, 1f);
            Vector3 flat = new(away.X, 0f, away.Z);
            return flat.LengthSquared > 1e-12f ? flat.Normalized() : -Vector3.UnitZ;
        }
    }

    // Screen right on the ground.
    public Vector3 GroundRight => Vector3.Normalize(Vector3.Cross(GroundForward, Vector3.UnitY));

    public static TacticalCameraPose Default(Vector3 target) =>
        new(target, IsometricView.YawDegrees, IsometricView.PitchDegrees, IsometricView.Distance);

    public void ResolveInto(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Vector3 toCamera = FocusToCamera;
        camera.Position = Target + toCamera;
        camera.SetFacing(-toCamera);
    }
}
