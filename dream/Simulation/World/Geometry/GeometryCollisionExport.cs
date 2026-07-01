namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryCollisionExport(
    IReadOnlyList<GeometryCellKey> DirtyCells,
    IReadOnlyList<GeometryCollisionBodyDescriptor> Bodies);
