namespace Dream.Simulation.World.Geometry;

public interface IGeometryMutationService
{
    GeometryMutationResult SetChildSolid(
        GeometryCellRecord record,
        GeometrySubcellKey child,
        bool isSolid);
}
