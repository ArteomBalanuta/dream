using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

public static class PlayerMovementResolver
{
    public static (Vector3 Direction, float VerticalDirection) Resolve(
        Camera camera,
        bool noclipEnabled,
        bool moveForward,
        bool moveBackward,
        bool moveLeft,
        bool moveRight,
        bool moveUp,
        bool moveDown)
    {
        Vector3 direction = Vector3.Zero;
        float verticalDirection = 0f;

        Vector3 forward = noclipEnabled
            ? camera.Front.Normalized()
            : new Vector3(camera.Front.X, 0f, camera.Front.Z).Normalized();
        Vector3 right = new Vector3(camera.Right.X, 0f, camera.Right.Z).Normalized();

        if (moveForward) direction += forward;
        if (moveBackward) direction -= forward;
        if (moveLeft) direction -= right;
        if (moveRight) direction += right;

        if (direction != Vector3.Zero)
        {
            direction = direction.Normalized();
        }

        if (noclipEnabled)
        {
            if (moveUp)
            {
                verticalDirection += 1f;
            }

            if (moveDown)
            {
                verticalDirection -= 1f;
            }
        }

        return (direction, verticalDirection);
    }
}
