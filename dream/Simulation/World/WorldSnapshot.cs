using OpenTK.Mathematics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;


public readonly record struct PlacedCubeSnapshot(
    Vector3 CellPosition,
    Vector3 CenterPosition,
    GeometryMaterialId MaterialId = GeometryMaterialId.Grass,
    int? ActorId = null);

public readonly record struct FracturedCubeSnapshot(
    Vector3 CellPosition,
    GeometryMaterialId MaterialId,
    IReadOnlyList<GeometryFracturePiece> Pieces);

public readonly record struct WorldSnapshot(
    int Width,
    int Height,
    int Depth,
    IReadOnlyList<Vector3> PlacedCubeCells,
    IReadOnlyList<Vector3>? FracturedCubeCells = null,
    IReadOnlyList<Vector3>? AuthoredSubcellCells = null,
    IReadOnlyList<PlacedCubeSnapshot>? PlacedCubes = null,
    IReadOnlyList<FracturedCubeSnapshot>? FracturedCubes = null,
    // Last world mutation sequence already reflected in this world; clients acknowledge it so
    // queued deltas up to it are not replayed on top.
    ulong? MutationSequence = null);
