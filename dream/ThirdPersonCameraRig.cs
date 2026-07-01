using OpenTK.Mathematics;

public static class ThirdPersonCameraRig
{
    private const float FollowDistance = 4.5f;
    private const float FollowHeight = 1.6f;
    private const float LookTargetHeight = 1.05f;

    public static Camera Resolve(Camera firstPersonCamera, Vector3 playerWorldPosition, CameraViewMode mode)
    {
        Camera camera = firstPersonCamera.Clone();
        if (mode == CameraViewMode.FirstPerson)
        {
            return camera;
        }

        Vector3 horizontalForward = Flatten(firstPersonCamera.Front);
        if (horizontalForward == Vector3.Zero)
        {
            horizontalForward = -Vector3.UnitZ;
        }

        Vector3 target = playerWorldPosition + new Vector3(0f, LookTargetHeight, 0f);
        camera.Position = target - (horizontalForward * FollowDistance) + (Vector3.UnitY * FollowHeight);
        camera.SetFacing(target - camera.Position);
        return camera;
    }

    private static Vector3 Flatten(Vector3 vector)
    {
        Vector3 flattened = new(vector.X, 0f, vector.Z);
        return flattened.LengthSquared <= 0.0001f ? Vector3.Zero : flattened.Normalized();
    }
}
