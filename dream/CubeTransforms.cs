using BulletSharp.Math;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

public static class CubeTransforms
{
    public static Matrix4 CreateCenteredVisualModelMatrix(Vector3 center, float side)
    {
        return Matrix4.CreateScale(side) * Matrix4.CreateTranslation(center);
    }

    public static Matrix4 CreateCollisionDebugModelMatrix(Matrix bulletWorldTransform)
    {
        return bulletWorldTransform.ToOpenTK();
    }
}
