using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

// The material choice the "default" generator has always made, kept byte-identical: a modulo-hash
// scatter over 3x3 cell blocks. MapSourceGoldenTests pins its output, so nothing here may change;
// new distributions go in a new policy behind a new generator key (NaturalTerrainMaterialPolicy).
public sealed class ClassicTerrainMaterialPolicy : ITerrainMaterialPolicy
{
    public static ClassicTerrainMaterialPolicy Instance { get; } = new();

    public GeometryMaterialId ResolveFloorMaterial(int x, int z, int width, int depth)
    {
        int rimDistance = TerrainLandmarks.RimDistance(x, z, width, depth);
        bool centralPath = TerrainLandmarks.IsCentralPath(x, z, width, depth);
        bool spawnPath = TerrainLandmarks.IsSpawnPath(x, z, width, depth);

        if (rimDistance <= 2)
        {
            return rimDistance == 0
                ? GeometryMaterialId.Stone
                : GeometryMaterialId.Rock;
        }

        if (spawnPath || centralPath)
        {
            return spawnPath
                ? GeometryMaterialId.Dirt
                : GeometryMaterialId.GrassPath;
        }

        int terrainHash = Hash(x / 3, z / 3);
        if ((terrainHash % 29) == 0)
        {
            return GeometryMaterialId.Sand;
        }

        if ((terrainHash % 23) == 0)
        {
            return GeometryMaterialId.Mud;
        }

        if ((terrainHash % 11) <= 1)
        {
            return GeometryMaterialId.Dirt;
        }

        if ((terrainHash % 17) == 0)
        {
            return GeometryMaterialId.Stone;
        }

        return GeometryMaterialId.Grass;
    }

    public GeometryMaterialId ResolveRaisedMaterial(int x, int z, int width, int depth, int y, int raisedHeight)
    {
        if (y < raisedHeight)
        {
            return GeometryMaterialId.Stone;
        }

        if (TerrainLandmarks.IsSpawnBridge(x, z))
        {
            return GeometryMaterialId.Dirt;
        }

        if (TerrainLandmarks.IsSpawnIsland(x, z))
        {
            if (TerrainLandmarks.SpawnIslandRimDistance(x, z) <= 1)
            {
                return GeometryMaterialId.Stone;
            }

            int islandHash = Hash(x / 2, z / 2);
            return (islandHash % 5) == 0
                ? GeometryMaterialId.Dirt
                : GeometryMaterialId.Grass;
        }

        return GeometryMaterialId.Stone;
    }

    private static int Hash(int x, int z)
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + x;
            hash = (hash * 31) + z;
            return Math.Abs(hash);
        }
    }
}
