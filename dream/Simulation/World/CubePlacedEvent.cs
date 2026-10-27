namespace Dream.Simulation.World;

using OpenTK.Mathematics;
using Dream.Simulation.World.Geometry;

public readonly record struct CubePlacedEvent(
    Cube Cube,
    Vector3 CellPosition,
    GeometryMaterialId MaterialId = GeometryMaterialId.Grass);
