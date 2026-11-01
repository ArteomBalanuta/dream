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
