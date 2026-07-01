using Dream.Simulation.World;

namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryRenderCell(
    GeometryCellKey Key,
    GeometryCellRecord Cell,
    Cube CompatibilityCube);
