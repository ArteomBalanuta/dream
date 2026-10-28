using OpenTK.Mathematics;

namespace Dream.Simulation.Enemies;


public static class WolfSpawnPolicy
{
    public const float FloorSurfaceY = 1f;
    private static readonly Vector3 DefaultSpawn = new(5.5f, FloorSurfaceY, 2.5f);

    // A night spawn closer than this (horizontally) to the day spawn counts as the same place.
    private const float MinimumSpawnSeparation = 1f;

    // Tried in order when the night spawn lands on the day spawn: a step along X, then along Z.
    private static readonly Vector2[] NightSpawnOffsets =
    [
        new(2f, 0f),
        new(-2f, 0f),
        new(0f, 2f),
        new(0f, -2f)
    ];

    public static Vector3 ResolveInitialSpawn(int width, int depth)
    {
        return new Vector3(
            ClampAxis(DefaultSpawn.X, width),
            FloorSurfaceY,
            ClampAxis(DefaultSpawn.Z, depth));
    }

    // The second wolf a night adds: the day spawn mirrored into the opposite corner,
    // (width - 5.5, 1, depth - 2.5), clamped like the day spawn. On a map where that lands on the
    // day spawn (small or symmetric), it steps 2 cells away from the day spawn instead; a map too
    // small for any step keeps the clamped corner. Pure, so every server picks the same cell.
    public static Vector3 ResolveNightSpawn(int width, int depth)
    {
        Vector3 day = ResolveInitialSpawn(width, depth);
        Vector3 night = new(
            ClampAxis(width - DefaultSpawn.X, width),
            FloorSurfaceY,
            ClampAxis(depth - DefaultSpawn.Z, depth));
        if (!Overlaps(night, day))
        {
            return night;
        }

        foreach (Vector2 offset in NightSpawnOffsets)
        {
            Vector3 candidate = new(
                ClampAxis(day.X + offset.X, width),
                FloorSurfaceY,
                ClampAxis(day.Z + offset.Y, depth));
            if (!Overlaps(candidate, day))
            {
                return candidate;
            }
        }

        return night;
    }

    private static bool Overlaps(Vector3 left, Vector3 right)
    {
        float dx = left.X - right.X;
        float dz = left.Z - right.Z;
        return (dx * dx) + (dz * dz) < MinimumSpawnSeparation * MinimumSpawnSeparation;
    }

    private static float ClampAxis(float desiredCenter, int size)
    {
        float min = 0.5f;
        float max = Math.Max(min, size - 1.5f);
        return Math.Clamp(desiredCenter, min, max);
    }
}
