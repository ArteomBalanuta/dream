using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector3 = OpenTK.Mathematics.Vector3;

public class PlayerController
{
    public MovementCommand CaptureMovement(
        Camera camera,
        KeyboardState keyboardState,
        bool allowGameplayInput,
        bool noclipEnabled)
    {
        if (!allowGameplayInput)
        {
            return new MovementCommand(Vector3.Zero, 0f, false, false, false);
        }

        (Vector3 movementDirection, float verticalDirection) = PlayerMovementResolver.Resolve(
            camera,
            noclipEnabled,
            moveForward: keyboardState.IsKeyDown(Keys.W),
            moveBackward: keyboardState.IsKeyDown(Keys.S),
            moveLeft: keyboardState.IsKeyDown(Keys.A),
            moveRight: keyboardState.IsKeyDown(Keys.D),
            moveUp: keyboardState.IsKeyDown(Keys.Space),
            moveDown: keyboardState.IsKeyDown(Keys.LeftControl));

        return new MovementCommand(
            movementDirection,
            verticalDirection,
            keyboardState.IsKeyDown(Keys.Space),
            keyboardState.IsKeyDown(Keys.F),
            keyboardState.IsKeyDown(Keys.V));
    }

    public static MovementCommand CreateMovementCommand(
        Vector3 cameraFront,
        Vector3 cameraRight,
        bool moveForward,
        bool moveBackward,
        bool moveLeft,
        bool moveRight,
        bool jumpPressed,
        bool freezePressed,
        bool allowGameplayInput)
    {
        if (!allowGameplayInput)
        {
            return new MovementCommand(Vector3.Zero, 0f, false, false, false);
        }

        Vector3 movementDirection = Vector3.Zero;
        Vector3 forward = FlattenToWorldPlane(cameraFront);
        Vector3 right = FlattenToWorldPlane(cameraRight);

        if (moveForward) movementDirection += forward;
        if (moveBackward) movementDirection -= forward;
        if (moveLeft) movementDirection -= right;
        if (moveRight) movementDirection += right;

        if (movementDirection != Vector3.Zero)
        {
            movementDirection = movementDirection.Normalized();
        }

        return new MovementCommand(
            movementDirection,
            0f,
            jumpPressed,
            freezePressed,
            false);
    }

    private static Vector3 FlattenToWorldPlane(Vector3 direction)
    {
        Vector3 flattened = new(direction.X, 0f, direction.Z);
        return flattened == Vector3.Zero ? Vector3.Zero : flattened.Normalized();
    }

    public PlayerInteractionCommand CaptureInteraction(
        KeyboardState keyboardState,
        MouseState mouseState,
        bool allowGameplayInput)
    {
        return CreateInteractionCommand(
            mouseState.IsButtonDown(MouseButton.Left),
            keyboardState.IsKeyDown(Keys.D1),
            keyboardState.IsKeyDown(Keys.D2),
            allowGameplayInput);
    }

    public static PlayerInteractionCommand CreateInteractionCommand(bool primaryUsePressed, bool allowGameplayInput)
    {
        return new PlayerInteractionCommand(
            PrimaryUsePressed: allowGameplayInput && primaryUsePressed,
            SelectBuildingModePressed: false,
            SelectCombatModePressed: false);
    }

    public static PlayerInteractionCommand CreateInteractionCommand(
        bool primaryUsePressed,
        bool selectBuildingModePressed,
        bool selectCombatModePressed,
        bool allowGameplayInput)
    {
        return new PlayerInteractionCommand(
            PrimaryUsePressed: allowGameplayInput && primaryUsePressed,
            SelectBuildingModePressed: allowGameplayInput && selectBuildingModePressed,
            SelectCombatModePressed: allowGameplayInput && selectCombatModePressed);
    }
}
