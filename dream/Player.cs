using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

public class Player
{
    public Vector3 Position { get; set; } // Player position
    public Vector3 Velocity { get; set; } = Vector3.Zero; // Player velocity
    public float Speed { get; set; } = 5.0f; // Speed in blocks per second

    // Player dimensions
    public float Width { get; } = 1.0f; // 1 block wide
    public float Height { get; } = 1.0f; // 1 block tall

    // Camera attached to the player
    public Camera Camera { get; private set; }

    public Player(Vector3 position)
    {
        Position = position;
        Camera = new Camera(position);
    }

    public void Update(float deltaTime, Map map, KeyboardState keyboardState, MouseState mouseState)
    {
        // Reset velocity
        Velocity = Vector3.Zero;

        // Calculate movement direction based on camera orientation
        Vector3 movementDirection = Vector3.Zero;

        // Horizontal movement (forward/backward, left/right)
        if (keyboardState.IsKeyDown(Keys.W))
        {
            movementDirection += new Vector3(Camera.Front.X, 0, Camera.Front.Z).Normalized(); // Move forward (horizontal)
        }
        if (keyboardState.IsKeyDown(Keys.S))
        {
            movementDirection -= new Vector3(Camera.Front.X, 0, Camera.Front.Z).Normalized(); // Move backward (horizontal)
        }
        if (keyboardState.IsKeyDown(Keys.A))
        {
            movementDirection -= Camera.Right; // Strafe left
        }
        if (keyboardState.IsKeyDown(Keys.D))
        {
            movementDirection += Camera.Right; // Strafe right
        }

        // Vertical movement (up/down)
        if (keyboardState.IsKeyDown(Keys.Space))
        {
            movementDirection += Vector3.UnitY; // Move up
        }
        if (keyboardState.IsKeyDown(Keys.LeftShift))
        {
            movementDirection -= Vector3.UnitY; // Move down
        }

        // Normalize movement direction to prevent faster diagonal movement
        if (movementDirection != Vector3.Zero)
        {
            movementDirection = Vector3.Normalize(movementDirection);
        }

        // Calculate new position
        Vector3 newPosition = Position + movementDirection * Speed * deltaTime;

        // Check for collisions before updating the position
        if (!map.CheckCollision(newPosition, Width, Height))
        {
            Position = newPosition;
        }

        // Update the camera's position to follow the player
        Camera.Position = new Vector3(Position.X, Position.Y, Position.Z);

        // Update the camera's orientation based on mouse input
        Camera.Update(deltaTime, mouseState);
    }
}