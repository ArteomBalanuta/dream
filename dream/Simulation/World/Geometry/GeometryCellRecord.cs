namespace Dream.Simulation.World.Geometry;

public sealed class GeometryCellRecord
{
    public GeometryCellRecord(
        GeometryCellKey key,
        GeometryCellDefinition definition,
        GeometrySubdivisionState? subdivisionState = null)
    {
        Key = key;
        Definition = definition;
        SubdivisionState = subdivisionState;
    }

    public GeometryCellKey Key { get; }

    public GeometryCellDefinition Definition { get; }

    public GeometrySubdivisionState? SubdivisionState { get; private set; }

    public bool IsChildSolid(GeometrySubcellKey key)
    {
        if (SubdivisionState != null && SubdivisionState.TryGetChildSolid(key, out bool overriddenSolid))
        {
            return overriddenSolid;
        }

        return GeometryShapeOccupancy.IsChildSolid(Definition, key);
    }

    public GeometrySubdivisionState EnsureSubdivisionState()
    {
        if (SubdivisionState == null)
        {
            SubdivisionState = new GeometrySubdivisionState(Definition.SubdivisionResolution);
        }

        return SubdivisionState;
    }
}
