using OpenTK.Mathematics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.Effects;

// Finds the ground under a player for the contact blob: the top of the first solid geometry at or
// below the feet. Cells are [i, i + 1), so a full cell at y has its top face at y + 1; a subdivided
// cell (sculpture, rubble) is scanned per child, so its top is the top of the highest solid child
// of the column under the sample point. Allocation free.
public static class FxGroundProbe
{
    public const int MaxCellsDown = 8;
    // When the ground under the centre is lower than this below the feet, the blob is at a ledge
    // and the neighbouring cells within the capsule radius are probed too.
    public const float LedgeMargin = 0.15f;

    private const float TopTolerance = 0.01f;

    public static bool TryFindGroundY(Map map, Vector3 feet, out float groundY)
    {
        return TryFindGroundY(map, feet, 0f, out groundY);
    }

    // With a radius, a centre probe that lands more than LedgeMargin below the feet also probes the
    // columns the capsule overlaps and takes the highest top at or below the feet.
    public static bool TryFindGroundY(Map map, Vector3 feet, float radius, out float groundY)
    {
        ArgumentNullException.ThrowIfNull(map);
        groundY = 0f;
        if (!float.IsFinite(feet.X) || !float.IsFinite(feet.Y) || !float.IsFinite(feet.Z))
        {
            return false;
        }

        bool found = ProbeColumn(map, feet.X, feet.Z, feet.Y, out float best);
        if (radius > 0f && float.IsFinite(radius) && (!found || best < feet.Y - LedgeMargin))
        {
            int minX = (int)MathF.Floor(feet.X - radius);
            int maxX = (int)MathF.Floor(feet.X + radius);
            int minZ = (int)MathF.Floor(feet.Z - radius);
            int maxZ = (int)MathF.Floor(feet.Z + radius);
            for (int cx = minX; cx <= maxX; cx++)
            {
                for (int cz = minZ; cz <= maxZ; cz++)
                {
                    // The point of the column nearest the centre must be inside the capsule.
                    float sx = Math.Clamp(feet.X, cx, cx + 0.999f);
                    float sz = Math.Clamp(feet.Z, cz, cz + 0.999f);
                    float dx = sx - feet.X;
                    float dz = sz - feet.Z;
                    if (dx * dx + dz * dz > radius * radius)
                    {
                        continue;
                    }

                    if (ProbeColumn(map, sx, sz, feet.Y, out float top) && (!found || top > best))
                    {
                        best = top;
                        found = true;
                    }
                }
            }
        }

        if (!found)
        {
            return false;
        }

        groundY = MathF.Min(best, feet.Y);
        return true;
    }

    // The top of the highest solid geometry at or below feetY in the column through (x, z).
    private static bool ProbeColumn(Map map, float x, float z, float feetY, out float top)
    {
        top = 0f;
        int cx = (int)MathF.Floor(x);
        int cz = (int)MathF.Floor(z);
        int startY = (int)MathF.Floor(feetY - TopTolerance);
        for (int y = startY; y > startY - MaxCellsDown; y--)
        {
            GeometryCellRecord? record = map.TryGetGeometryCellAtCell(cx, y, cz);
            if (record == null)
            {
                continue;
            }

            if (record.SubdivisionState == null && record.Definition.ShapeKind == GeometryShapeKind.SolidCube)
            {
                top = y + 1;
                return true;
            }

            int resolution = record.SubdivisionState?.Resolution ?? record.Definition.SubdivisionResolution;
            if (resolution < 1)
            {
                continue;
            }

            int childX = Math.Clamp((int)((x - cx) * resolution), 0, resolution - 1);
            int childZ = Math.Clamp((int)((z - cz) * resolution), 0, resolution - 1);
            for (int k = resolution - 1; k >= 0; k--)
            {
                float childTop = y + (k + 1) / (float)resolution;
                if (childTop > feetY + TopTolerance || !record.IsChildSolid(new GeometrySubcellKey(childX, k, childZ)))
                {
                    continue;
                }

                top = childTop;
                return true;
            }
        }

        return false;
    }
}
