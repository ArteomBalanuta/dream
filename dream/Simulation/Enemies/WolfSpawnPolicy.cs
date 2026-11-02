using OpenTK.Mathematics;

namespace Dream.Simulation.Enemies;


public static class WolfSpawnPolicy
{
    public const float FloorSurfaceY = 1f;

    // The wolves spawn in the far corner from the world spawn (5, 5): the day wolf this far in from the
    // map's far edges, the night's second wolf a little closer to the corner. On the 30 m map the day wolf is
    // 30 m from the world spawn and the night one 32 m, past every template's aggro range (10 and 12 m), so
    // a player who joins and stands still is not attacked at once. Maps under about 16 m are too small to
    // keep that distance; the spawns then clamp inside the map as close to the corner as they go.
    private const float DayInsetFromFarCorner = 3.5f;
    private const float NightInsetFromFarCorner = 2.5f;

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

    // The members of a camp with more than one enemy stand on a ring around its centre, one slot each
    // (slot modulo the table). A single-member camp stands on its centre.
    private static readonly Vector2[] CampSlotOffsets =
    [
        new(1.5f, 0f),
        new(-1.5f, 0f),
        new(0f, 1.5f),
        new(0f, -1.5f),
        new(1.1f, 1.1f),
        new(-1.1f, 1.1f),
        new(1.1f, -1.1f),
        new(-1.1f, -1.1f)
    ];

    // Where member slot of a camp stands: its centre (x, z) plus the slot's ring offset, on the floor,
    // clamped inside the map like the wolf spawns. Pure, so every server picks the same position.
    public static Vector3 ResolveCampSpawn(float x, float z, int slot, int campCount, int width, int depth)
    {
        Vector2 offset = campCount > 1
            ? CampSlotOffsets[((slot % CampSlotOffsets.Length) + CampSlotOffsets.Length) % CampSlotOffsets.Length]
            : Vector2.Zero;
        return new Vector3(
            ClampAxis(x + offset.X, width),
            FloorSurfaceY,
            ClampAxis(z + offset.Y, depth));
    }

    public static Vector3 ResolveInitialSpawn(int width, int depth)
    {
        return new Vector3(
            ClampAxis(width - DayInsetFromFarCorner, width),
            FloorSurfaceY,
            ClampAxis(depth - DayInsetFromFarCorner, depth));
    }

    // The second wolf a night adds: (width - 2.5, 1, depth - 2.5), clamped like the day spawn. On a map
    // where that lands on the day spawn (a very small one), it steps 2 cells away from the day spawn
    // instead; a map too small for any step keeps the clamped corner. Pure, so every server picks the same cell.
    public static Vector3 ResolveNightSpawn(int width, int depth)
    {
        Vector3 day = ResolveInitialSpawn(width, depth);
        Vector3 night = new(
            ClampAxis(width - NightInsetFromFarCorner, width),
            FloorSurfaceY,
            ClampAxis(depth - NightInsetFromFarCorner, depth));
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
