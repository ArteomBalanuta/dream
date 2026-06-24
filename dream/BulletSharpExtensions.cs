using BulletSharp.Math;
using OpenTK.Mathematics;

public static class BulletSharpExtensions
{
    public static Matrix4 ToOpenTK(this Matrix bulletMatrix)
    {
        return new Matrix4(
            bulletMatrix.M11, bulletMatrix.M12, bulletMatrix.M13, bulletMatrix.M14,
            bulletMatrix.M21, bulletMatrix.M22, bulletMatrix.M23, bulletMatrix.M24,
            bulletMatrix.M31, bulletMatrix.M32, bulletMatrix.M33, bulletMatrix.M34,
            bulletMatrix.M41, bulletMatrix.M42, bulletMatrix.M43, bulletMatrix.M44
        );
    }
}
