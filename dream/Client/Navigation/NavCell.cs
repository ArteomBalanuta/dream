using OpenTK.Mathematics;

namespace Dream.Client.Navigation;

public readonly record struct NavCell(int X, int Level, int Z)
{
    public Vector3 Centre => new(X + 0.5f, Level + 0.5f, Z + 0.5f);

    public static NavCell FromBody(Vector3 bodyCentre)
        => new((int)MathF.Floor(bodyCentre.X), (int)MathF.Floor(bodyCentre.Y), (int)MathF.Floor(bodyCentre.Z));
}
