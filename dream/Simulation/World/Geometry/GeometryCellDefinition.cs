namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryCellDefinition(
    GeometryShapeKind ShapeKind,
    GeometryMaterialId MaterialId,
    int SubdivisionResolution,
    bool IsSolid = true,
    bool IsFloor = false)
{
    public bool SupportsSubdivision => SubdivisionResolution > 1;
}
