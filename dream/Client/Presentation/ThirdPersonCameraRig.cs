using OpenTK.Mathematics;
using Dream.Application;
using Dream.Simulation.Players;

namespace Dream.Client.Presentation;


public static class ThirdPersonCameraRig
{
    private const float FollowDistance = 4.5f;
    private const float FollowHeight = 1.6f;
    private const float AimTargetDistance = 8f;

    // Returns a new camera. Per-frame callers that do not hand the result out should keep one
    // Camera and use ResolveInto instead.
    public static Camera Resolve(Camera firstPersonCamera, Vector3 playerWorldPosition, CameraViewMode mode)
    {
        ArgumentNullException.ThrowIfNull(firstPersonCamera);
        Camera camera = new(firstPersonCamera.Position);
        ResolveInto(firstPersonCamera, playerWorldPosition, mode, camera);
        return camera;
    }

    // Allocation-free form of Resolve: writes the resolved camera into target, which must not be
    // firstPersonCamera. Camera.CopyTo produces the same camera Clone did.
    public static void ResolveInto(Camera firstPersonCamera, Vector3 playerWorldPosition, CameraViewMode mode, Camera target)
    {
        ArgumentNullException.ThrowIfNull(firstPersonCamera);
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(firstPersonCamera, target))
        {
            throw new ArgumentException("target must be a different camera than firstPersonCamera.", nameof(target));
        }

        firstPersonCamera.CopyTo(target);
        if (mode == CameraViewMode.FirstPerson)
        {
            return;
        }

        Vector3 horizontalForward = firstPersonCamera.HorizontalForward;
        Vector3 followAnchor = firstPersonCamera.Position;
        Vector3 aimTarget = followAnchor + (firstPersonCamera.Front * AimTargetDistance);
        target.Position = followAnchor - (horizontalForward * FollowDistance) + (Vector3.UnitY * FollowHeight);
        target.SetFacing(aimTarget - target.Position);
    }
}
