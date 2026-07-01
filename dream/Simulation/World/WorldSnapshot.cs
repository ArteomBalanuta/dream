using OpenTK.Mathematics;

namespace Dream.Simulation.World;


public readonly record struct WorldSnapshot(
    int Width,
    int Height,
    int Depth,
    IReadOnlyList<Vector3> PlacedCubeCells);
