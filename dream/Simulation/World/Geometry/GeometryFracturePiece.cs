using OpenTK.Mathematics;

namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryFracturePiece(
    Vector3 Center,
    float SideLength)
{
    public const float MinSideLength = 0.05f;
    public const float MaxSideLength = 1f;

    public bool IsValid()
    {
        return IsFinite(Center.X) &&
            IsFinite(Center.Y) &&
            IsFinite(Center.Z) &&
            IsFinite(SideLength) &&
            SideLength >= MinSideLength &&
            SideLength <= MaxSideLength;
    }

    public static bool IsValid(GeometryFracturePiece piece)
    {
        return piece.IsValid();
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
