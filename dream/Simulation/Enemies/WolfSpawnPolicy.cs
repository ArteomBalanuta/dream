using OpenTK.Mathematics;
using Dream.Simulation.Content;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

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

    // spawns.json is authored against a map of this size (the shipped 56 m world); on any other size a camp's
    // x and z scale by size / DesignMapSize, so a camp keeps its place relative to the map.
    public const float DesignMapSize = 56f;

    // A member is placed at most this many ring steps (1.5 m each) from its first choice.
    private const int MaxCampRingSteps = 4;

    // A member whose cell is solid is not lifted onto it (wolves never change level, so it would hover once it
    // walked off): it relocates to another candidate.
    private const float MaxCampLift = 0f;

    // Where member slot of a camp stands: its centre (x, z), scaled to the map, plus the slot's ring offset,
    // on the floor, clamped inside the map like the wolf spawns. A first choice only: it may be solid, or
    // near the world spawn (TryResolveCampSpawn checks). Pure, so every server picks the same position.
    public static Vector3 ResolveCampSpawn(float x, float z, int slot, int campCount, int width, int depth)
    {
        return CampCandidate(x, z, slot, campCount, width, depth, ringStep: 1, ringIndex: slot);
    }

    private static Vector3 CampCandidate(float x, float z, int slot, int campCount, int width, int depth, int ringStep, int ringIndex)
    {
        Vector2 offset = campCount > 1 || ringStep > 1
            ? CampSlotOffsets[((ringIndex % CampSlotOffsets.Length) + CampSlotOffsets.Length) % CampSlotOffsets.Length] * ringStep
            : Vector2.Zero;
        return new Vector3(
            ClampAxis((x * width / DesignMapSize) + offset.X, width),
            FloorSurfaceY,
            ClampAxis((z * depth / DesignMapSize) + offset.Y, depth));
    }

    // The standing place for member slot of a camp on map: the first free candidate (the slot's ring spot, then
    // the other ring spots at growing radius) that is not solid (the body box ResolveRespawnPosition tests),
    // is farther than aggroRange from worldSpawn, and is not within
    // MinimumSpawnSeparation of any position in occupied. False when no candidate qualifies: the member is
    // skipped, so a small world may hold fewer than the camp's count. Pure over (map, arguments).
    internal static bool TryResolveCampSpawn(
        Map map,
        SpawnCamp camp,
        int slot,
        Vector3 worldSpawn,
        float aggroRange,
        IReadOnlyList<Vector3> occupied,
        out Vector3 position)
    {
        for (int step = 1; step <= MaxCampRingSteps; step++)
        {
            for (int j = 0; j < CampSlotOffsets.Length; j++)
            {
                // Step 1 starts at the slot's own ring spot; a single-member camp's first try is its centre.
                if (step == 1 && j == 0 && camp.Count <= 1)
                {
                    if (TryAccept(map, CampCandidate(camp.X, camp.Z, slot, 1, map.Width, map.Depth, 1, 0), worldSpawn, aggroRange, occupied, out position))
                    {
                        return true;
                    }
                }

                Vector3 candidate = CampCandidate(camp.X, camp.Z, slot, 2, map.Width, map.Depth, step, slot + j);
                if (TryAccept(map, candidate, worldSpawn, aggroRange, occupied, out position))
                {
                    return true;
                }
            }
        }

        position = default;
        return false;
    }

    private static bool TryAccept(
        Map map,
        Vector3 candidate,
        Vector3 worldSpawn,
        float aggroRange,
        IReadOnlyList<Vector3> occupied,
        out Vector3 position)
    {
        position = candidate;
        float sx = candidate.X - worldSpawn.X;
        float sz = candidate.Z - worldSpawn.Z;
        if ((sx * sx) + (sz * sz) <= aggroRange * aggroRange)
        {
            return false;
        }

        for (int i = 0; i < occupied.Count; i++)
        {
            if (Overlaps(candidate, occupied[i]))
            {
                return false;
            }
        }

        // The body-box test of a respawn, centred half a cell above the feet.
        Vector3 resolved = WorldSessionRuntime.ResolveRespawnPosition(map, new Vector3(candidate.X, candidate.Y + 0.5f, candidate.Z));
        float lift = resolved.Y - 0.5f - candidate.Y;
        if (lift > MaxCampLift + 0.001f || resolved.Y >= map.Height - 0.5f)
        {
            return false;
        }

        position = new Vector3(candidate.X, candidate.Y + MathF.Max(0f, lift), candidate.Z);
        return true;
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
