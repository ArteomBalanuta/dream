using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector3 = OpenTK.Mathematics.Vector3;

public class PlayerController
{
    public MovementCommand CaptureMovement(Camera camera, KeyboardState keyboardState, bool allowGameplayInput)
    {
        if (!allowGameplayInput)
        {
            return new MovementCommand(Vector3.Zero, false, false);
        }

        Vector3 movementDirection = Vector3.Zero;
        Vector3 forward = new Vector3(camera.Front.X, 0f, camera.Front.Z).Normalized();
        Vector3 right = new Vector3(camera.Right.X, 0f, camera.Right.Z).Normalized();

        if (keyboardState.IsKeyDown(Keys.W)) movementDirection += forward;
        if (keyboardState.IsKeyDown(Keys.S)) movementDirection -= forward;
        if (keyboardState.IsKeyDown(Keys.A)) movementDirection -= right;
        if (keyboardState.IsKeyDown(Keys.D)) movementDirection += right;

        if (movementDirection != Vector3.Zero)
        {
            movementDirection = movementDirection.Normalized();
        }

        return new MovementCommand(
            movementDirection,
            keyboardState.IsKeyDown(Keys.Space),
            keyboardState.IsKeyDown(Keys.F));
    }

    public PlayerInteractionCommand CaptureInteraction(MouseState mouseState, bool allowGameplayInput)
    {
        return new PlayerInteractionCommand(
            allowGameplayInput && mouseState.IsButtonDown(MouseButton.Left));
    }
}
