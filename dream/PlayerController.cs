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

    public PlayerInteractionCommand CaptureInteraction(
        KeyboardState keyboardState,
        MouseState mouseState,
        bool allowGameplayInput)
    {
        return new PlayerInteractionCommand(
            PrimaryUsePressed: allowGameplayInput && mouseState.IsButtonDown(MouseButton.Left),
            SelectBuildingModePressed: allowGameplayInput && keyboardState.IsKeyDown(Keys.D1),
            SelectCombatModePressed: allowGameplayInput && keyboardState.IsKeyDown(Keys.D2));
    }
}
