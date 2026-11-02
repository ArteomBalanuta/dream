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
            // A fixed ring of eight points at the radius, so children of the feet's own cell are
            // seen and the cost does not depend on the radius.
            float d = radius * 0.70710678f;
            for (int i = 0; i < 8; i++)
            {
                float ox = i switch { 0 => radius, 1 => d, 2 => 0f, 3 => -d, 4 => -radius, 5 => -d, 6 => 0f, _ => d };
                float oz = i switch { 0 => 0f, 1 => d, 2 => radius, 3 => d, 4 => 0f, 5 => -d, 6 => -radius, _ => -d };
                if (ProbeColumn(map, feet.X + ox, feet.Z + oz, feet.Y, out float top) && (!found || top > best))
                {
                    best = top;
                    found = true;
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
                // A top above the feet (a wall beside a jump) or covered by more solid is no surface.
                if (y + 1 <= feetY + TopTolerance && !SolidAtBottom(map, x, z, cx, y + 1, cz))
                {
                    top = y + 1;
                    return true;
                }

                continue;
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

                bool covered = k + 1 < resolution
                    ? record.IsChildSolid(new GeometrySubcellKey(childX, k + 1, childZ))
                    : SolidAtBottom(map, x, z, cx, y + 1, cz);
                if (covered)
                {
                    continue;
                }

                top = childTop;
                return true;
            }
        }

        return false;
    }

    // Whether the lowest slice of the cell at (cx, y, cz) is solid under the point (x, z).
    private static bool SolidAtBottom(Map map, float x, float z, int cx, int y, int cz)
    {
        GeometryCellRecord? record = map.TryGetGeometryCellAtCell(cx, y, cz);
        if (record == null)
        {
            return false;
        }

        if (record.SubdivisionState == null && record.Definition.ShapeKind == GeometryShapeKind.SolidCube)
        {
            return true;
        }

        int resolution = record.SubdivisionState?.Resolution ?? record.Definition.SubdivisionResolution;
        if (resolution < 1)
        {
            return false;
        }

        int childX = Math.Clamp((int)((x - cx) * resolution), 0, resolution - 1);
        int childZ = Math.Clamp((int)((z - cz) * resolution), 0, resolution - 1);
        return record.IsChildSolid(new GeometrySubcellKey(childX, 0, childZ));
    }
}
