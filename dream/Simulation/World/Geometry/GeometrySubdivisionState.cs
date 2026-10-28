namespace Dream.Simulation.World.Geometry;

// Sparse per-child solidity overrides on top of the cell's shape occupancy. A cell can also be
// marked all-empty (rubble: every child in the resolution's range is overridden to empty)
// without storing an override per child; explicit overrides still take precedence.
public sealed class GeometrySubdivisionState
{
    private Dictionary<GeometrySubcellKey, bool>? _childSolidOverrides;

    public GeometrySubdivisionState(int resolution)
    {
        Resolution = resolution;
    }

    public int Resolution { get; }

    // True when every child in [0, Resolution)^3 without an explicit override reads as empty.
    public bool AllChildrenEmpty { get; private set; }

    public bool HasOverrides => (AllChildrenEmpty && Resolution > 0) || _childSolidOverrides is { Count: > 0 };

    // Same count as storing one override per child: all in-range children when the state is
    // all-empty, plus explicit overrides of keys outside that range.
    public int OverrideCount
    {
        get
        {
            if (!AllChildrenEmpty)
            {
                return _childSolidOverrides?.Count ?? 0;
            }

            int count = Resolution > 0 ? Resolution * Resolution * Resolution : 0;
            if (_childSolidOverrides != null)
            {
                foreach (GeometrySubcellKey key in _childSolidOverrides.Keys)
                {
                    if (!IsInRange(key))
                    {
                        count++;
                    }
                }
            }

            return count;
        }
    }

    public bool TryGetChildSolid(GeometrySubcellKey key, out bool isSolid)
    {
        if (_childSolidOverrides != null && _childSolidOverrides.TryGetValue(key, out isSolid))
        {
            return true;
        }

        if (AllChildrenEmpty && IsInRange(key))
        {
            isSolid = false;
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

    // Equivalent to SetChildSolid(key, false) for every key in [0, Resolution)^3, without
    // allocating: explicit in-range overrides are dropped, overrides outside the range are kept.
    public void SetAllChildrenEmpty()
    {
        AllChildrenEmpty = true;
        if (_childSolidOverrides is not { Count: > 0 })
        {
            return;
        }

        List<GeometrySubcellKey>? inRange = null;
        foreach (GeometrySubcellKey key in _childSolidOverrides.Keys)
        {
            if (IsInRange(key))
            {
                (inRange ??= []).Add(key);
            }
        }

        if (inRange != null)
        {
            foreach (GeometrySubcellKey key in inRange)
            {
                _childSolidOverrides.Remove(key);
            }
        }
    }

    private bool IsInRange(GeometrySubcellKey key)
    {
        return (uint)key.X < (uint)Resolution &&
            (uint)key.Y < (uint)Resolution &&
            (uint)key.Z < (uint)Resolution;
    }
}
