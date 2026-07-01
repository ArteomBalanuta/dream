namespace Dream.Simulation.World.Geometry;

public sealed class GeometrySubdivisionState
{
    private Dictionary<GeometrySubcellKey, bool>? _childSolidOverrides;

    public GeometrySubdivisionState(int resolution)
    {
        Resolution = resolution;
    }

    public int Resolution { get; }

    public bool HasOverrides => _childSolidOverrides is { Count: > 0 };

    public int OverrideCount => _childSolidOverrides?.Count ?? 0;

    public bool TryGetChildSolid(GeometrySubcellKey key, out bool isSolid)
    {
        if (_childSolidOverrides != null && _childSolidOverrides.TryGetValue(key, out isSolid))
        {
            return true;
        }

        isSolid = default;
        return false;
    }

    public void SetChildSolid(GeometrySubcellKey key, bool isSolid)
    {
        _childSolidOverrides ??= new Dictionary<GeometrySubcellKey, bool>();
        _childSolidOverrides[key] = isSolid;
    }
}
