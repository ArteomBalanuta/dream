using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

public class Camera
{
    public Vector3 Position { get; set; }
    public Vector3 Front { get; private set; } = -Vector3.UnitZ;
    public Vector3 Up { get; private set; } = Vector3.UnitY;
    public Vector3 Right { get; private set; }

    private float _pitch; // Rotation around the X axis (up and down)
    private float _yaw = -OpenTK.Mathematics.MathHelper.PiOver2; // Rotation around the Y axis (left and right)

    private float _mouseSensitivity = 5.0f;

    public Camera(Vector3 position)
    {
       
        Vector3 cameraUpABit = new Vector3(position.X , position.Y, position.Z);
        Position = cameraUpABit;

        UpdateVectors();
    }

    public void Update(float deltaTime, MouseState mouseState)
    {
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