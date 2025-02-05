using System;
using System.Collections.Generic;
using BulletSharp;
using BulletSharp.Math; // For Bullet physics math types.
using OpenTK.Mathematics; // For your game’s math types.

// Conversion helpers between BulletSharp.Math.Vector3 and OpenTK.Mathematics.Vector3
public static class VectorConversion
{
    public static BulletSharp.Math.Vector3 ToBullet(this OpenTK.Mathematics.Vector3 v)
    {
        return new BulletSharp.Math.Vector3(v.X, v.Y, v.Z);
    }

    public static OpenTK.Mathematics.Vector3 ToOpenTK(this BulletSharp.Math.Vector3 v)
    {
        return new OpenTK.Mathematics.Vector3(v.X, v.Y, v.Z);
    }

    public static Matrix ConvertVector3ToMatrix(BulletSharp.Math.Vector3 position)
    {
        // Create a transformation matrix with the position as translation
        return new Matrix(
            1, 0, 0, 0,    // First row
            0, 1, 0, 0,    // Second row
            0, 0, 1, 0,    // Third row
            position.X, position.Y, position.Z, 1 // Translation
        );
    }
}