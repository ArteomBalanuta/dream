namespace Dream.Simulation.World;

using OpenTK.Mathematics;
using Dream.Simulation.World.Geometry;

public readonly record struct CubeFracturedEvent(
    Cube Cube,
    Vector3 CellPosition,
    GeometryMaterialId MaterialId,
    IReadOnlyList<GeometryFracturePiece> Pieces);
