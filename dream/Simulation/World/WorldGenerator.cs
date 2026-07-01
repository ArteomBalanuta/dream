using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;


public class WorldGenerator
{
    private const int DefaultSubdivisionResolution = 10;
    private const int DefaultSpawnClearRadius = 4;
    private readonly bool _loadVisualAssets;

    public WorldGenerator(bool loadVisualAssets)
    {
        _loadVisualAssets = loadVisualAssets;
    }

    public GeometryCellRecord CreateGeometryCell(Vector3 cellPosition, bool isFloor)
    {
        return CreateGeometryCell(cellPosition, isFloor, GeometryMaterialId.Grass);
    }

    public GeometryCellRecord CreateGeometryCell(
        Vector3 cellPosition,
        bool isFloor,
        GeometryMaterialId materialId)
    {
        GeometryCellKey key = new GeometryCellKey(
            (int)cellPosition.X,
            (int)cellPosition.Y,
            (int)cellPosition.Z);

        GeometryCellDefinition definition = new GeometryCellDefinition(
            GeometryShapeKind.SolidCube,
            materialId,
            DefaultSubdivisionResolution,
            IsSolid: true,
            IsFloor: isFloor);

        return new GeometryCellRecord(key, definition);
    }

    public IReadOnlyList<GeometryCellRecord> CreateStartupGeometry(int width, int height, int depth, bool enrichTerrain)
    {
        List<GeometryCellRecord> cells = new(width * depth);
        bool useEnrichedTerrain = enrichTerrain && width >= 20 && depth >= 20;

        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                cells.Add(CreateStartupFloorCell(x, z, width, depth, useEnrichedTerrain));

                int raisedHeight = useEnrichedTerrain
                    ? ResolveRaisedLandmarkHeight(x, z, width, depth)
                    : 0;
                for (int y = 1; y <= raisedHeight && y < height; y++)
                {
                    cells.Add(CreateRaisedLandmarkCell(
                        x,
                        y,
                        z,
                        ResolveRaisedMaterial(x, z, width, depth, y, raisedHeight)));
                }
            }
        }

        return cells;
    }

    public Cube CreateCompatibilityCube(GeometryCellRecord geometryCell)
    {
        return new Cube
        {
            Position = new Vector3(geometryCell.Key.X + 0.5f, geometryCell.Key.Y + 0.5f, geometryCell.Key.Z + 0.5f),
            IsEmpty = false,
            IsSolid = true,
            Animation = ResolveAnimation(geometryCell.Definition.MaterialId),
            ShaderName = "cube"
        };
    }

    public GeometryRenderBlockDescriptor CreateRenderBlockDescriptor(
        GeometryCellRecord geometryCell,
        Vector3 center,
        float sideLength,
        GeometrySubcellKey? subcellKey = null)
    {
        return new GeometryRenderBlockDescriptor(
            geometryCell.Key,
            center,
            sideLength,
            geometryCell.Definition.MaterialId,
            ResolveAnimation(geometryCell.Definition.MaterialId),
            "cube",
            subcellKey);
    }

    public Cube CreateSolidCube(Vector3 cellPosition)
    {
        return CreateCompatibilityCube(CreateGeometryCell(cellPosition, isFloor: cellPosition.Y <= 0f));
    }

    private GeometryCellRecord CreateStartupFloorCell(int x, int z, int width, int depth, bool useEnrichedTerrain)
    {
        GeometryCellDefinition definition = new GeometryCellDefinition(
            GeometryShapeKind.SolidCube,
            useEnrichedTerrain
                ? ResolveFloorMaterial(x, z, width, depth)
                : GeometryMaterialId.Grass,
            DefaultSubdivisionResolution,
            IsSolid: true,
            IsFloor: true);

        return new GeometryCellRecord(new GeometryCellKey(x, 0, z), definition);
    }

    private GeometryCellRecord CreateRaisedLandmarkCell(int x, int y, int z, GeometryMaterialId materialId)
    {
        GeometryCellDefinition definition = new GeometryCellDefinition(
            GeometryShapeKind.SolidCube,
            materialId,
            DefaultSubdivisionResolution,
            IsSolid: true,
            IsFloor: false);

        return new GeometryCellRecord(new GeometryCellKey(x, y, z), definition);
    }

    private GeometryMaterialId ResolveFloorMaterial(int x, int z, int width, int depth)
    {
        int rimDistance = Math.Min(Math.Min(x, z), Math.Min((width - 1) - x, (depth - 1) - z));
        int centerX = width / 2;
        int centerZ = depth / 2;
        const int spawnX = 5;
        const int spawnZ = 5;
        bool centralPath = Math.Abs(x - centerX) <= 1 || Math.Abs(z - centerZ) <= 1;
        bool spawnPath = (Math.Abs(x - spawnX) <= 1 && z <= centerZ) ||
            (Math.Abs(z - spawnZ) <= 1 && x <= centerX);

        if (rimDistance <= 2)
        {
            return GeometryMaterialId.Stone;
        }

        if (spawnPath || centralPath)
        {
            return GeometryMaterialId.Dirt;
        }

        int terrainHash = Hash(x / 3, z / 3);
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

    private int ResolveRaisedLandmarkHeight(int x, int z, int width, int depth)
    {
        if (x <= DefaultSpawnClearRadius && z <= DefaultSpawnClearRadius)
        {
            return 0;
        }

        int height = 0;
        height = Math.Max(height, ResolveSpawnIslandHeight(x, z));
        height = Math.Max(height, ResolveTerracedMoundHeight(x, z, 10, 8, radius: 2, maxHeight: 2));
        height = Math.Max(height, ResolveTerracedMoundHeight(x, z, 8, 12, radius: 2, maxHeight: 1));
        height = Math.Max(height, ResolveTerracedMoundHeight(x, z, width / 2, depth / 2, radius: 3, maxHeight: 2));
        height = Math.Max(height, ResolveTerracedMoundHeight(x, z, width - 8, depth / 3, radius: 2, maxHeight: 2));
        height = Math.Max(height, ResolveTerracedMoundHeight(x, z, width / 3, depth - 8, radius: 2, maxHeight: 2));
        return height;
    }

    private static int ResolveSpawnIslandHeight(int x, int z)
    {
        bool bridge = (x == 7 || x == 8) && z >= 4 && z <= 6;
        if (bridge)
        {
            return 1;
        }

        bool outerIsland = x >= 9 && x <= 18 && z >= 3 && z <= 14;
        if (!outerIsland)
        {
            return 0;
        }

        bool innerRise = x >= 11 && x <= 15 && z >= 6 && z <= 11;
        return innerRise ? 2 : 1;
    }

    private GeometryMaterialId ResolveRaisedMaterial(int x, int z, int width, int depth, int y, int raisedHeight)
    {
        if (y < raisedHeight)
        {
            return GeometryMaterialId.Stone;
        }

        bool bridge = (x == 7 || x == 8) && z >= 4 && z <= 6;
        if (bridge)
        {
            return GeometryMaterialId.Dirt;
        }

        if (x >= 9 && x <= 18 && z >= 3 && z <= 14)
        {
            int rimDistance = Math.Min(Math.Min(x - 9, 18 - x), Math.Min(z - 3, 14 - z));
            if (rimDistance <= 1)
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

    private static int ResolveTerracedMoundHeight(int x, int z, int centerX, int centerZ, int radius, int maxHeight)
    {
        int distance = Math.Abs(x - centerX) + Math.Abs(z - centerZ);
        if (distance > radius)
        {
            return 0;
        }

        return Math.Max(1, maxHeight - (distance / 2));
    }

    private AnimatedModel? ResolveAnimation(GeometryMaterialId materialId)
    {
        if (!_loadVisualAssets)
        {
            return null;
        }

        return materialId switch
        {
            GeometryMaterialId.Dirt => ModelAssets.DirtCube,
            GeometryMaterialId.Stone => ModelAssets.StoneCube,
            _ => ModelAssets.GrassCube
        };
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
