namespace Dream.Simulation.World.Geometry;

public sealed class GeometryMutationService : IGeometryMutationService
{
    public GeometryMutationResult SetChildSolid(
        GeometryCellRecord record,
        GeometrySubcellKey child,
        bool isSolid)
    {
        bool subdivisionActivated = record.SubdivisionState == null;
        GeometrySubdivisionState subdivision = record.EnsureSubdivisionState();
        subdivision.SetChildSolid(child, isSolid);

        GeometryCellKey[] changedCells = [record.Key];
        return new GeometryMutationResult(
            ChangedCells: changedCells,
            SubdivisionActivated: subdivisionActivated,
            RenderDirtyCells: changedCells,
            CollisionDirtyCells: changedCells,
            RemovedCells: Array.Empty<GeometryCellKey>());
    }
}
