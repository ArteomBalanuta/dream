namespace Dream.Simulation.Players;

﻿using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

public class Camera
{
    private const float MouseSensitivityNormalization = 1f / 60f;
    private const float HorizontalForwardEpsilon = 0.0001f;

    public Vector3 Position { get; set; }
    public Vector3 Front { get; private set; } = -Vector3.UnitZ;
    public Vector3 Up { get; private set; } = Vector3.UnitY;
    public Vector3 Right { get; private set; }
    public Vector3 HorizontalForward
    {
        get
        {
            Vector3 horizontal = new(MathF.Cos(_yaw), 0f, MathF.Sin(_yaw));
            return horizontal.LengthSquared <= HorizontalForwardEpsilon
                ? -Vector3.UnitZ
                : horizontal.Normalized();
        }
    }

    private float _pitch; // Rotation around the X axis (up and down)
    private float _yaw = -OpenTK.Mathematics.MathHelper.PiOver2; // Rotation around the Y axis (left and right)

    public float MouseSensitivity { get; set; } = 2.5f;

    public Camera(Vector3 position)
    {
       
        Vector3 cameraUpABit = new Vector3(position.X , position.Y, position.Z);
        Position = cameraUpABit;

        UpdateVectors();
    }

    public void Update(float deltaTime, Vector2 lookDelta)
    {
        _ = deltaTime;
        ApplyLookDelta(lookDelta);
    }

    public void ApplyLookDelta(Vector2 lookDelta)
    {
        float adjustedSensitivity = MouseSensitivity * MouseSensitivityNormalization;
        _yaw += lookDelta.X * adjustedSensitivity;
        _pitch -= lookDelta.Y * adjustedSensitivity;

        // Clamp pitch to avoid flipping
        _pitch = OpenTK.Mathematics.MathHelper.Clamp(_pitch, -OpenTK.Mathematics.MathHelper.PiOver2, OpenTK.Mathematics.MathHelper.PiOver2);

        // Update camera vectors
        UpdateVectors();
    }

    public void SetFacing(Vector3 facing)
    {
        if (facing.LengthSquared <= 0.000001f)
        {
            return;
        }

        Vector3 normalizedFacing = facing.Normalized();
        _pitch = MathF.Asin(Math.Clamp(normalizedFacing.Y, -1f, 1f));
        _yaw = MathF.Atan2(normalizedFacing.Z, normalizedFacing.X);
        UpdateVectors();
    }

    public Camera Clone()
    {
        Camera clone = new(Position)
        {
            MouseSensitivity = MouseSensitivity
        };
        clone.SetFacing(Front);
        return clone;
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
