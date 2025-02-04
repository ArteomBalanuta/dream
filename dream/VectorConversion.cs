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
}