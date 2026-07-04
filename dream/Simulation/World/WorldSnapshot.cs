using OpenTK.Mathematics;

namespace Dream.Simulation.World;


public readonly record struct PlacedCubeSnapshot(
    Vector3 CellPosition,
    Vector3 CenterPosition);

public readonly record struct WorldSnapshot(
    int Width,
    int Height,
    int Depth,
    IReadOnlyList<Vector3> PlacedCubeCells,
    IReadOnlyList<Vector3>? FracturedCubeCells = null,
    IReadOnlyList<Vector3>? AuthoredSubcellCells = null,
    IReadOnlyList<PlacedCubeSnapshot>? PlacedCubes = null);
