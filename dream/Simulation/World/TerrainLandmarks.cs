namespace Dream.Simulation.World;

// The hand-placed, gameplay-legible features of a startup world, shared by every material policy
// so none of them can lose a path or a bridge. These are shapes, not materials.
public static class TerrainLandmarks
{
    public const int SpawnX = 5;
    public const int SpawnZ = 5;

    // The plank from the spawn corner onto the raised island.
    public static bool IsSpawnBridge(int x, int z)
    {
        return (x == 7 || x == 8) && z >= 4 && z <= 6;
    }

    public static bool IsSpawnIsland(int x, int z)
    {
        return x >= 9 && x <= 18 && z >= 3 && z <= 14;
    }

    // Cells from the island's edge; only meaningful inside IsSpawnIsland.
    public static int SpawnIslandRimDistance(int x, int z)
    {
        return Math.Min(Math.Min(x - 9, 18 - x), Math.Min(z - 3, 14 - z));
    }

    // Cells from the world's edge.
    public static int RimDistance(int x, int z, int width, int depth)
    {
        return Math.Min(Math.Min(x, z), Math.Min((width - 1) - x, (depth - 1) - z));
    }

    // The walkable spine of the world: the two central lanes.
    public static bool IsCentralPath(int x, int z, int width, int depth)
    {
        return Math.Abs(x - (width / 2)) <= 1 || Math.Abs(z - (depth / 2)) <= 1;
    }

    // The lanes that lead out of the spawn corner toward the centre.
    public static bool IsSpawnPath(int x, int z, int width, int depth)
    {
        return (Math.Abs(x - SpawnX) <= 1 && z <= depth / 2) ||
            (Math.Abs(z - SpawnZ) <= 1 && x <= width / 2);
    }
}
