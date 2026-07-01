using OpenTK.Mathematics;
using Dream.Application;
using Dream.Simulation.Players;

namespace Dream.Client.Presentation;


public static class ThirdPersonCameraRig
{
    private const float FollowDistance = 4.5f;
    private const float FollowHeight = 1.6f;
    private const float AimTargetDistance = 8f;

    public static Camera Resolve(Camera firstPersonCamera, Vector3 playerWorldPosition, CameraViewMode mode)
    {
        Camera camera = firstPersonCamera.Clone();
        if (mode == CameraViewMode.FirstPerson)
        {
            return camera;
        }

        Vector3 horizontalForward = firstPersonCamera.HorizontalForward;
        Vector3 followAnchor = firstPersonCamera.Position;
        Vector3 target = followAnchor + (firstPersonCamera.Front * AimTargetDistance);
        camera.Position = followAnchor - (horizontalForward * FollowDistance) + (Vector3.UnitY * FollowHeight);
        camera.SetFacing(target - camera.Position);
        return camera;
    }
}
