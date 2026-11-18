using OpenTK.Mathematics;
using Dream.Simulation.World;

namespace Dream.Simulation.Enemies;


public static class EnemyMovementCollision
{
    private const float SweepStepLength = 0.10f;
    private const float OccupancyEpsilon = 0.001f;

    public static bool CanOccupy(Map map, Vector3 position, float radius, float height)
    {
        float resolvedRadius = Math.Max(0f, radius);
        float resolvedHeight = Math.Max(0.1f, height);

        if (position.X - resolvedRadius < 0f ||
            position.Z - resolvedRadius < 0f ||
            position.X + resolvedRadius > map.Width ||
            position.Z + resolvedRadius > map.Depth)
        {
            return false;
        }

        int minX = (int)MathF.Floor(position.X - resolvedRadius + OccupancyEpsilon);
        int maxX = (int)MathF.Floor(position.X + resolvedRadius - OccupancyEpsilon);
        int minZ = (int)MathF.Floor(position.Z - resolvedRadius + OccupancyEpsilon);
        int maxZ = (int)MathF.Floor(position.Z + resolvedRadius - OccupancyEpsilon);
        int minY = Math.Max(1, (int)MathF.Floor(position.Y + OccupancyEpsilon));
        int maxY = Math.Min(map.Height - 1, (int)MathF.Floor(position.Y + resolvedHeight - OccupancyEpsilon));

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    if (map.TryGetCubeAtCell(x, y, z) != null)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    public static Vector3 ResolveStep(Map map, Vector3 from, Vector3 desiredDelta, float radius, float height)
    {
        if (desiredDelta.LengthSquared <= 0.000001f)
        {
            return Vector3.Zero;
        }

        // An enemy that already overlaps geometry - a cube was built on the cell it stands in, which the
        // build path does not refuse, or a restore clamped it into a wall - has no free candidate anywhere
        // along the sweep, so the sweep used to return zero and the enemy was wedged there for good while
        // still holding its camp slot. It escapes instead (see ResolveEscapeStep).
        if (!CanOccupy(map, from, radius, height))
        {
            return ResolveEscapeStep(map, from, desiredDelta, radius, height);
        }

        int steps = Math.Max(1, (int)MathF.Ceiling(desiredDelta.Length / SweepStepLength));
        Vector3 lastAllowed = from;

        for (int i = 1; i <= steps; i++)
        {
            Vector3 candidate = from + (desiredDelta * (i / (float)steps));
            if (!CanOccupy(map, candidate, radius, height))
            {
                break;
            }

            lastAllowed = candidate;
        }

        return lastAllowed - from;
    }

    // BH-REV1. Returning the asked-for delta was an escape hatch with no direction and no bound: the chase
    // vector points at the player, so a wedged enemy took its full requested step every tick while any part
    // of it was still inside and simply traversed a one- or two-cell wall into whatever lay beyond. It now
    // resolves OUT of the geometry instead of through it:
    //  - the asked-for step is taken only when it ENDS outside geometry (a genuine exit, not a tunnel);
    //  - otherwise the enemy steps to the nearest free spot, searched ring by ring outwards, preferring the
    //    direction away from the step that wedged it; the search is bounded, so it never crosses a wall.
    // Fixed candidate order, fixed ring order, pure float arithmetic: no wall clock and no hash ordering,
    // so lockstep peers resolve identically.
    private static readonly Vector3[] EscapeDirections =
    [
        new(1f, 0f, 0f),
        new(-1f, 0f, 0f),
        new(0f, 0f, 1f),
        new(0f, 0f, -1f),
        new(0.70710678f, 0f, 0.70710678f),
        new(-0.70710678f, 0f, 0.70710678f),
        new(0.70710678f, 0f, -0.70710678f),
        new(-0.70710678f, 0f, -0.70710678f),
    ];

    private const float EscapeRingLength = 0.25f;
    private const int EscapeRings = 8;

    private static Vector3 ResolveEscapeStep(Map map, Vector3 from, Vector3 desiredDelta, float radius, float height)
    {
        if (CanOccupy(map, from + desiredDelta, radius, height))
        {
            return desiredDelta;
        }

        Vector3 away = -(desiredDelta / desiredDelta.Length);
        for (int ring = 1; ring <= EscapeRings; ring++)
        {
            float distance = ring * EscapeRingLength;
            int best = -1;
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < EscapeDirections.Length; i++)
            {
                if (!CanOccupy(map, from + (EscapeDirections[i] * distance), radius, height))
                {
                    continue;
                }

                float score = Vector3.Dot(EscapeDirections[i], away);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            if (best >= 0)
            {
                return EscapeDirections[best] * distance;
            }
        }

        return Vector3.Zero;
    }

    // True when no cube lies on the horizontal line from the wolf (at its body-middle height) to the player,
    // sampled every 0.25 m. Cubes lower than the player's feet cell are ignored: the player stands on them,
    // so a wolf at the foot of a 1-cube step still reaches. Allocation-free; at most a handful of lookups.
    public static bool HasClearBiteLine(Map map, Vector3 wolfPosition, float wolfAnchorHeight, Vector3 playerPosition, float playerHalfHeight)
    {
        const float sampleSpacing = 0.25f;
        int layer = (int)MathF.Floor(wolfPosition.Y + wolfAnchorHeight);
        int playerFeetLayer = (int)MathF.Floor(playerPosition.Y - playerHalfHeight + OccupancyEpsilon);
        if (layer < playerFeetLayer)
        {
            return true;
        }

        float dx = playerPosition.X - wolfPosition.X;
        float dz = playerPosition.Z - wolfPosition.Z;
        float length = MathF.Sqrt((dx * dx) + (dz * dz));
        int samples = (int)MathF.Ceiling(length / sampleSpacing);
        for (int i = 1; i < samples; i++)
        {
            float t = i / (float)samples;
            int cx = (int)MathF.Floor(wolfPosition.X + (dx * t));
            int cz = (int)MathF.Floor(wolfPosition.Z + (dz * t));
            if (map.TryGetCubeAtCell(cx, layer, cz) != null)
            {
                return false;
            }
        }

        return true;
    }
}
