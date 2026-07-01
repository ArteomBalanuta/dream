namespace Dream.Simulation.World.Geometry;

public static class GeometryShapeOccupancy
{
    public static bool IsChildSolid(
        GeometryCellDefinition definition,
        GeometrySubcellKey child)
    {
        return definition.ShapeKind switch
        {
            GeometryShapeKind.SolidCube => definition.IsSolid,
            GeometryShapeKind.Slope => child.Y <= (definition.SubdivisionResolution - 1 - child.Z),
            GeometryShapeKind.Wedge => child.Y <= child.X,
            _ => definition.IsSolid
        };
    }
}
