using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

// Running WorldStateChecksum. Every write to _geometryCells and _rubbleCells goes through the
// helpers below, which subtract the replaced record's contribution and add the new one's, and
// ApplyGeometrySubcellSolid re-hashes the record around its in-place subdivision change. The
// checksum is a sum, so the value is the same as WorldStateChecksum.ComputeFull's full walk in any
// order of edits (FindInvariantViolations checks it).
//
// In-place record changes that cannot change a record's contribution are not re-hashed: moving
// rubble pieces (SetFracturePieces on a rubble record keeps FracturePieces non-null) and the
// subdivision SetRubble applies before the record is stored.
public partial class Map
{
    private ulong _worldChecksumSum;
    private ulong _worldChecksumCount;

    // WorldStateChecksum.Compute(this), maintained incrementally; O(1).
    public ulong WorldChecksum => WorldStateChecksum.Finish(_worldChecksumSum, _worldChecksumCount);

    // WorldStateChecksum.ComputeFull: the full walk over both stores, through the concrete
    // dictionaries' struct enumerators.
    internal ulong ComputeWorldChecksumFull()
    {
        ulong sum = 0;
        ulong count = 0;
        foreach (GeometryCellRecord record in _geometryCells.Values)
        {
            if (WorldStateChecksum.TryHashCell(record, out ulong cellHash))
            {
                sum = unchecked(sum + cellHash);
                count++;
            }
        }

        foreach (GeometryCellRecord record in _rubbleCells.Values)
        {
            if (WorldStateChecksum.TryHashCell(record, out ulong cellHash))
            {
                sum = unchecked(sum + cellHash);
                count++;
            }
        }

        return WorldStateChecksum.Finish(sum, count);
    }

    private void SetGeometryCell(GeometryCellKey key, GeometryCellRecord record)
    {
        if (_geometryCells.TryGetValue(key, out GeometryCellRecord? previous))
        {
            UntrackChecksum(previous);
        }

        _geometryCells[key] = record;
        TrackChecksum(record);
    }

    private bool RemoveGeometryCell(GeometryCellKey key)
    {
        if (!_geometryCells.Remove(key, out GeometryCellRecord? removed))
        {
            return false;
        }

        UntrackChecksum(removed);
        return true;
    }

    private void SetRubbleCell(GeometryCellKey key, GeometryCellRecord record)
    {
        if (_rubbleCells.TryGetValue(key, out GeometryCellRecord? previous))
        {
            UntrackChecksum(previous);
        }

        _rubbleCells[key] = record;
        TrackChecksum(record);
    }

    private bool RemoveRubbleCell(GeometryCellKey key)
    {
        if (!_rubbleCells.Remove(key, out GeometryCellRecord? removed))
        {
            return false;
        }

        UntrackChecksum(removed);
        return true;
    }

    private void TrackChecksum(GeometryCellRecord record)
    {
        if (WorldStateChecksum.TryHashCell(record, out ulong cellHash))
        {
            _worldChecksumSum = unchecked(_worldChecksumSum + cellHash);
            _worldChecksumCount++;
        }
    }

    private void UntrackChecksum(GeometryCellRecord record)
    {
        if (WorldStateChecksum.TryHashCell(record, out ulong cellHash))
        {
            _worldChecksumSum = unchecked(_worldChecksumSum - cellHash);
            _worldChecksumCount--;
        }
    }
}
