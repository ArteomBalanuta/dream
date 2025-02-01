
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

public class Camera
{
    public Vector3 Position { get; private set; }
    public Vector3 Front { get; private set; } = -Vector3.UnitZ;
    public Vector3 Up { get; private set; } = Vector3.UnitY;
    public Vector3 Right { get; private set; }

    private float _pitch; // Rotation around the X axis (up and down)
    private float _yaw = -OpenTK.Mathematics.MathHelper.PiOver2; // Rotation around the Y axis (left and right)

    private float _movementSpeed = 5.0f;
    private float _mouseSensitivity = 3.0f;

    private Map _map; // Reference to the map for collision detection

    public Camera(Vector3 position, Map map)
    {
        Position = position;
        _map = map;
        UpdateVectors();
    }

    public void Update(float deltaTime, KeyboardState keyboardState, MouseState mouseState)
    {
        // Handle keyboard input for movement
        Vector3 newPosition = Position;

        if (keyboardState.IsKeyDown(Keys.W))
        {
            newPosition += Front * _movementSpeed * deltaTime;
        }
        if (keyboardState.IsKeyDown(Keys.S))
        {
            newPosition -= Front * _movementSpeed * deltaTime;
        }
        if (keyboardState.IsKeyDown(Keys.A))
        {
            newPosition -= Right * _movementSpeed * deltaTime;
        }
        if (keyboardState.IsKeyDown(Keys.D))
        {
            newPosition += Right * _movementSpeed * deltaTime;
        }

        // Check for collisions before updating the position
        if (!_map.CheckCollision(newPosition, 1.0f, 2.0f))
        { // Player is 1 block wide and 2 blocks tall
            Position = newPosition;
        }

        // Handle mouse input for looking around
        _yaw += mouseState.Delta.X * _mouseSensitivity * deltaTime;
        _pitch -= mouseState.Delta.Y * _mouseSensitivity * deltaTime;

        // Clamp pitch to avoid flipping
        _pitch = OpenTK.Mathematics.MathHelper.Clamp(_pitch, -OpenTK.Mathematics.MathHelper.PiOver2, OpenTK.Mathematics.MathHelper.PiOver2);

        // Update camera vectors
        UpdateVectors();
    }

    private void UpdateVectors()
    {
        // Calculate the new Front vector
        Vector3 newFront;
        newFront.X = MathF.Cos(_pitch) * MathF.Cos(_yaw);
        newFront.Y = MathF.Sin(_pitch);
        newFront.Z = MathF.Cos(_pitch) * MathF.Sin(_yaw);
        Front = Vector3.Normalize(newFront);

        // Recalculate the Right and Up vectors
        Right = Vector3.Normalize(Vector3.Cross(Front, Vector3.UnitY));
        Up = Vector3.Normalize(Vector3.Cross(Right, Front));
    }

    public Matrix4 GetViewMatrix()
    {
        return Matrix4.LookAt(Position, Position + Front, Up);
    }
}