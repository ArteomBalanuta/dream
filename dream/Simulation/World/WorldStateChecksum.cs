using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

// Order-independent fingerprint of the replicated world structure, computed identically on the
// server and on clients: every raised intact solid cube cell and every fractured cell, with
// its material. Exact centers and rubble positions are excluded (centers move continuously,
// and rubble is simulated cosmetically per client). The server sends it about once a second.
// A client whose fingerprint differs after applying the same mutations requests a full world
// resync instead of keeping a divergence until reconnect.
public static class WorldStateChecksum
{
    public static ulong Compute(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        ulong sum = 0;
        ulong count = 0;
        foreach (GeometryCellRecord record in map.GeometryCells.Values.Concat(map.RubbleCells.Values))
        {
            if (record.Key.Y <= 0)
            {
                continue;
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
                continue;
            }

            ulong cellHash = Mix(
                ((ulong)(uint)record.Key.X << 42) ^
                ((ulong)(uint)record.Key.Y << 21) ^
                (ulong)(uint)record.Key.Z ^
                (kind << 60) ^
                ((ulong)record.Definition.MaterialId << 52));
            sum = unchecked(sum + cellHash);
            count++;
        }

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
