using OpenTK.Mathematics;

public readonly record struct WorldSnapshot(
    int Width,
    int Height,
    int Depth,
    IReadOnlyList<Vector3> PlacedCubeCells);
