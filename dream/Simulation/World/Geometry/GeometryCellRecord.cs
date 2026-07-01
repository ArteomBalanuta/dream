namespace Dream.Simulation.World.Geometry;

public sealed class GeometryCellRecord
{
    public GeometryCellRecord(
        GeometryCellKey key,
        GeometryCellDefinition definition,
        GeometrySubdivisionState? subdivisionState = null,
        IReadOnlyList<GeometryFracturePiece>? fracturePieces = null)
    {
        Key = key;
        Definition = definition;
        SubdivisionState = subdivisionState;
        FracturePieces = fracturePieces;
    }

    public GeometryCellKey Key { get; }

    public GeometryCellDefinition Definition { get; }

    public GeometrySubdivisionState? SubdivisionState { get; private set; }

    public IReadOnlyList<GeometryFracturePiece>? FracturePieces { get; private set; }

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

    public void SetFracturePieces(IReadOnlyList<GeometryFracturePiece> fracturePieces)
    {
        FracturePieces = fracturePieces;
    }
}
