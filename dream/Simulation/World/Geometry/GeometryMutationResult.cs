namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryMutationResult(
    IReadOnlyList<GeometryCellKey> ChangedCells,
    bool SubdivisionActivated,
    IReadOnlyList<GeometryCellKey> RenderDirtyCells,
    IReadOnlyList<GeometryCellKey> CollisionDirtyCells,
    IReadOnlyList<GeometryCellKey> RemovedCells);
