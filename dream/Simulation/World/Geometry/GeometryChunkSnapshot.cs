namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryChunkSnapshot(
    IReadOnlyList<GeometryCellRecord> Cells);
