using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;

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

    // Store the last position to detect changes
    private Vector3 _lastPosition;

    public Player(Vector3 position)
    {
        Position = position;
        _lastPosition = position; // Initialize last position
        // Initialize the camera at the player's position
        Camera = new Camera(position);
    }

    public void Update(float deltaTime, Map map, KeyboardState keyboardState, MouseState mouseState)
    {
        // Reset velocity
        Velocity = Vector3.Zero;

        // Calculate movement direction based on camera orientation
        Vector3 movementDirection = Vector3.Zero;

        if (keyboardState.IsKeyDown(Keys.W))
        {
            movementDirection += Camera.Front; // Move forward (relative to camera)
        }
        if (keyboardState.IsKeyDown(Keys.S))
        {
            movementDirection -= Camera.Front; // Move backward (relative to camera)
        }
        if (keyboardState.IsKeyDown(Keys.A))
        {
            movementDirection -= Camera.Right; // Strafe left (relative to camera)
        }
        if (keyboardState.IsKeyDown(Keys.D))
        {
            movementDirection += Camera.Right; // Strafe right (relative to camera)
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
            // Log only if the position has changed
            if (newPosition != _lastPosition)
            {
                // Calculate moving speed (distance moved per second)
                float distanceMoved = Vector3.Distance(_lastPosition, newPosition);
                float movingSpeed = distanceMoved / deltaTime;

                // Log position change and moving speed
                Console.WriteLine($"Player moved to: ({newPosition.X:F2}, {newPosition.Y:F2}, {newPosition.Z:F2})");
                Console.WriteLine($"Moving speed: {movingSpeed:F2} blocks/second");

                // Log current standing block
                Vector3 standingBlockPosition = new Vector3((int)newPosition.X, (int)(newPosition.Y - 1), (int)newPosition.Z);
                if (map.IsInBounds(standingBlockPosition))
                {
                    Console.WriteLine($"Standing on block at: ({standingBlockPosition.X}, {standingBlockPosition.Y}, {standingBlockPosition.Z})");
                }

                // Update last position
                _lastPosition = newPosition;
            }

            Position = newPosition;
        }
        else
        {
            // Log collision
            Console.WriteLine("Collision detected! Player hit a wall or solid block.");
        }

        // Update the camera's position to follow the player
        Camera.Position = new Vector3(Position.X, Camera.Position.Y, Position.Z);

        // Update the camera's orientation based on mouse input
        Camera.Update(deltaTime, mouseState);
    }
}