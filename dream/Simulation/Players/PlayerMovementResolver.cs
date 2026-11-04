using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Simulation.Players;


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

    public static MovementCommand CreateMovementCommand(
        Camera camera,
        bool noclipEnabled,
        PlayerMoveIntent intent)
    {
        (Vector3 movementDirection, float verticalDirection) = ResolveMovementIntent(camera, noclipEnabled, intent);

        return new MovementCommand(
            movementDirection,
            verticalDirection,
            intent.JumpPressed,
            intent.FreezePressed,
            intent.ToggleNoclipPressed,
            ResolveThrottle(intent),
            intent.SprintHeld);
    }

    // How far the analog axes are pushed, measured before the move direction is normalized and
    // the magnitude is thrown away. Diagonals can exceed 1, so clamp.
    private static float ResolveThrottle(PlayerMoveIntent intent)
    {
        float magnitude = MathF.Sqrt(
            (intent.ForwardAxis * intent.ForwardAxis) + (intent.StrafeAxis * intent.StrafeAxis));
        return MathF.Min(1f, magnitude);
    }

    internal static Vector3 FlattenToWorldPlane(Vector3 direction)
    {
        Vector3 flattened = new(direction.X, 0f, direction.Z);
        return flattened == Vector3.Zero ? Vector3.Zero : flattened.Normalized();
    }

    private static (Vector3 Direction, float VerticalDirection) ResolveMovementIntent(
        Camera camera,
        bool noclipEnabled,
        PlayerMoveIntent intent)
    {
        Vector3 forward = noclipEnabled
            ? camera.Front.Normalized()
            : FlattenToWorldPlane(camera.Front);
        Vector3 right = FlattenToWorldPlane(camera.Right);
        Vector3 movementDirection = (forward * intent.ForwardAxis) + (right * intent.StrafeAxis);
        if (movementDirection != Vector3.Zero)
        {
            movementDirection = movementDirection.Normalized();
        }

        float verticalDirection = noclipEnabled ? intent.VerticalAxis : 0f;
        return (movementDirection, verticalDirection);
    }
}
