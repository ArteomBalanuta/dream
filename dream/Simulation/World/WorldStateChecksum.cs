using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

// Order-independent fingerprint of the replicated world structure, computed identically on the
// server and on clients: every raised intact solid cube cell and every fractured cell, with
// its material. Exact centers and rubble positions are excluded (centers move continuously,
// and rubble is simulated cosmetically per client). The server sends it about once a second.
// A client whose fingerprint differs after applying the same mutations requests a full world
// resync instead of keeping a divergence until reconnect.
//
// The value is Finish(sum of CellHash over the geometry and rubble records, number of hashed
// records). Addition is commutative and invertible, so Map keeps the sum and count up to date as
// records are set, replaced and removed (Map.Checksum.cs) and Compute reads them in O(1) without
// allocating. ComputeFull is the original full walk over both stores; it returns the same value
// and is the reference the incremental one is tested against (and checked by
// Map.FindInvariantViolations).
public static class WorldStateChecksum
{
    public static ulong Compute(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.WorldChecksum;
    }

    // Full walk over every geometry and rubble record. O(cells); the reference for Compute. Map
    // walks its own dictionaries: the same loop written here over the IReadOnlyDictionary.Values
    // interfaces returned a wrong sum in Release once tiered PGO had recompiled it (seen only when
    // a load test had run earlier in the process; the walk's inputs, re-summed elsewhere, matched).
    public static ulong ComputeFull(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.ComputeWorldChecksumFull();
    }

    // The record's contribution: false when the record is not part of the fingerprint (floor
    // cells, and carved or sculpted cells that are neither intact solid cubes nor rubble). It
    // reads only the key, the material, whether FracturePieces is set, whether SubdivisionState
    // is set and the shape kind, so Map re-hashes a record whenever one of those can change.
    internal static bool TryHashCell(GeometryCellRecord record, out ulong cellHash)
    {
        cellHash = 0;
        if (record.Key.Y <= 0)
        {
            return false;
        }

        ulong kind;
        if (record.FracturePieces != null)
        {
            kind = 2;
        }
        else if (record.SubdivisionState == null && record.Definition.ShapeKind == GeometryShapeKind.SolidCube)
        {
            kind = 1;
        }
        else
        {
            return false;
        }

        cellHash = Mix(
            ((ulong)(uint)record.Key.X << 42) ^
            ((ulong)(uint)record.Key.Y << 21) ^
            (ulong)(uint)record.Key.Z ^
            (kind << 60) ^
            ((ulong)record.Definition.MaterialId << 52));
        return true;
    }

    internal static ulong Finish(ulong sum, ulong count)
    {
        return Mix(sum ^ Mix(count));
    }

    // SplitMix64 finalizer.
    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
