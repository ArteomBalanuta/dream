using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;


public class WorldGenerator
{
    private const int DefaultSubdivisionResolution = 10;
    private const int SculptureSubdivisionResolution = WorldMicroGrid.MicroCellsPerWorldCell;
    private const int DefaultSpawnClearRadius = 4;

    public WorldGenerator()
    {
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
        Dictionary<GeometryCellKey, GeometryCellRecord> cells = new(width * depth);
        bool useEnrichedTerrain = enrichTerrain && width >= 20 && depth >= 20;

        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                AddOrReplaceCell(cells, CreateStartupFloorCell(x, z, width, depth, useEnrichedTerrain));

                int raisedHeight = useEnrichedTerrain
                    ? ResolveRaisedLandmarkHeight(x, z, width, depth)
                    : 0;
                for (int y = 1; y <= raisedHeight && y < height; y++)
                {
                    AddOrReplaceCell(cells, CreateRaisedLandmarkCell(
                        x,
                        y,
                        z,
                        ResolveRaisedMaterial(x, z, width, depth, y, raisedHeight)));
                }
            }
        }

        if (useEnrichedTerrain)
        {
            foreach (GeometryCellRecord sculptureCell in CreateStartupSculptureCells(width, height, depth))
            {
                AddOrReplaceCell(cells, sculptureCell);
            }
        }

        return cells.Values.ToArray();
    }

    public Cube CreateCompatibilityCube(GeometryCellRecord geometryCell)
    {
        return new Cube
        {
            Position = new Vector3(geometryCell.Key.X + 0.5f, geometryCell.Key.Y + 0.5f, geometryCell.Key.Z + 0.5f),
            MaterialId = geometryCell.Definition.MaterialId,
            IsEmpty = false,
            IsSolid = true,
            ShaderName = "cube"
        };
    }

    public GeometryRenderBlockDescriptor CreateRenderBlockDescriptor(
        GeometryCellRecord geometryCell,
        Vector3 center,
        float sideLength,
        GeometrySubcellKey? subcellKey = null,
        float visualFillScale = 1f,
        Vector3 sizeOverride = default)
    {
        return new GeometryRenderBlockDescriptor(
            geometryCell.Key,
            center,
            sideLength,
            geometryCell.Definition.MaterialId,
            "cube",
            subcellKey,
            visualFillScale,
            sizeOverride);
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

    private IReadOnlyList<GeometryCellRecord> CreateStartupSculptureCells(int width, int height, int depth)
    {
        Dictionary<GeometryCellKey, GeometryCellRecord> cells = [];
        if (width < 48 || depth < 32 || height < 6)
        {
            return cells.Values.ToArray();
        }

        AddSculptureCells(
            cells,
            new GeometryCellKey(14, 1, 12),
            new GeometryCellKey(23, 5, 21),
            width,
            height,
            depth,
            GeometryMaterialId.Stone,
            IsPyramidSubcell);

        AddSculptureCells(
            cells,
            new GeometryCellKey(32, 1, 12),
            new GeometryCellKey(37, 4, 17),
            width,
            height,
            depth,
            GeometryMaterialId.Dirt,
            IsRoundedBoulderSubcell);

        AddSculptureCells(
            cells,
            new GeometryCellKey(24, 1, 11),
            new GeometryCellKey(30, 3, 20),
            width,
            height,
            depth,
            GeometryMaterialId.Stone,
            IsLowRubbleClusterSubcell);

        return cells.Values.ToArray();
    }

    private static void AddOrReplaceCell(
        Dictionary<GeometryCellKey, GeometryCellRecord> cells,
        GeometryCellRecord record)
    {
        cells[record.Key] = record;
    }

    private static void AddSculptureCells(
        Dictionary<GeometryCellKey, GeometryCellRecord> cells,
        GeometryCellKey min,
        GeometryCellKey max,
        int width,
        int height,
        int depth,
        GeometryMaterialId materialId,
        Func<Vector3, bool> isSolid)
    {
        int maxCellX = Math.Min(max.X, width - 1);
        int maxCellY = Math.Min(max.Y, height - 1);
        int maxCellZ = Math.Min(max.Z, depth - 1);
        if (min.X > maxCellX || min.Y > maxCellY || min.Z > maxCellZ)
        {
            return;
        }

        WorldMicroCellKey minMicro = new(
            min.X * WorldMicroGrid.MicroCellsPerWorldCell,
            min.Y * WorldMicroGrid.MicroCellsPerWorldCell,
            min.Z * WorldMicroGrid.MicroCellsPerWorldCell);
        WorldMicroCellKey maxMicro = new(
            ((maxCellX + 1) * WorldMicroGrid.MicroCellsPerWorldCell) - 1,
            ((maxCellY + 1) * WorldMicroGrid.MicroCellsPerWorldCell) - 1,
            ((maxCellZ + 1) * WorldMicroGrid.MicroCellsPerWorldCell) - 1);

        HashSet<WorldMicroCellKey> solidMicrocells = [];
        for (int x = minMicro.X; x <= maxMicro.X; x++)
        {
            for (int y = minMicro.Y; y <= maxMicro.Y; y++)
            {
                for (int z = minMicro.Z; z <= maxMicro.Z; z++)
                {
                    WorldMicroCellKey microcell = new(x, y, z);
                    if (isSolid(WorldMicroGrid.ToWorldCenter(microcell)))
                    {
                        solidMicrocells.Add(microcell);
                    }
                }
            }
        }

        ProjectSculptureMicrocells(cells, solidMicrocells, materialId);
    }

    private static void ProjectSculptureMicrocells(
        Dictionary<GeometryCellKey, GeometryCellRecord> cells,
        HashSet<WorldMicroCellKey> solidMicrocells,
        GeometryMaterialId materialId)
    {
        Dictionary<GeometryCellKey, HashSet<GeometrySubcellKey>> microcellsByParent = [];
        foreach (WorldMicroCellKey microcell in solidMicrocells)
        {
            GeometryCellKey parent = WorldMicroGrid.ToParentCellKey(microcell);
            GeometrySubcellKey local = WorldMicroGrid.ToLocalSubcellKey(microcell);
            if (!microcellsByParent.TryGetValue(parent, out HashSet<GeometrySubcellKey>? localMicrocells))
            {
                localMicrocells = [];
                microcellsByParent[parent] = localMicrocells;
            }

            localMicrocells.Add(local);
        }

        foreach ((GeometryCellKey parent, HashSet<GeometrySubcellKey> localMicrocells) in microcellsByParent)
        {
            GeometrySubdivisionState subdivision = new(SculptureSubdivisionResolution);
            foreach (GeometrySubcellKey subcell in localMicrocells)
            {
                subdivision.SetChildSolid(subcell, isSolid: true);
            }

            GeometryCellDefinition definition = new(
                GeometryShapeKind.SolidCube,
                materialId,
                SculptureSubdivisionResolution,
                IsSolid: false,
                IsFloor: false);
            cells[parent] = new GeometryCellRecord(parent, definition, subdivision);
        }
    }

    private static bool IsPyramidSubcell(Vector3 center)
    {
        const float baseY = 1f;
        const float height = 4.35f;
        const float halfBase = 4.4f;
        const float centerX = 18.5f;
        const float centerZ = 16.5f;

        float localHeight = center.Y - baseY;
        if (localHeight < 0f || localHeight > height)
        {
            return false;
        }

        float allowedHalfWidth = halfBase * (1f - (localHeight / height));
        float dx = MathF.Abs(center.X - centerX);
        float dz = MathF.Abs(center.Z - centerZ);
        float boundaryDistance = allowedHalfWidth - MathF.Max(dx, dz);
        bool nearSide = boundaryDistance >= -WorldMicroGrid.MicroCellSize &&
            boundaryDistance <= WorldMicroGrid.MicroCellSize * 1.2f;
        bool nearBase = localHeight <= WorldMicroGrid.MicroCellSize;
        bool nearPeak = allowedHalfWidth <= WorldMicroGrid.MicroCellSize * 1.5f;
        return nearSide || nearBase || nearPeak;
    }

    private static bool IsRoundedBoulderSubcell(Vector3 center)
    {
        Vector3 boulderCenter = new(34.5f, 2.55f, 14.5f);
        Vector3 delta = center - boulderCenter;
        float stretchedDistance =
            (delta.X * delta.X) +
            ((delta.Y * 1.15f) * (delta.Y * 1.15f)) +
            (delta.Z * delta.Z);
        float normalized = stretchedDistance / (2.55f * 2.55f);
        return normalized <= 1f && normalized >= 0.84f;
    }

    private static bool IsLowRubbleClusterSubcell(Vector3 center)
    {
        return IsEllipsoidSubcell(center, new Vector3(25.4f, 1.34f, 12.8f), new Vector3(1.35f, 0.55f, 1.1f)) ||
            IsEllipsoidSubcell(center, new Vector3(27.2f, 1.45f, 14.4f), new Vector3(1.65f, 0.7f, 1.35f)) ||
            IsEllipsoidSubcell(center, new Vector3(29.2f, 1.32f, 17.6f), new Vector3(1.45f, 0.58f, 1.55f)) ||
            IsEllipsoidSubcell(center, new Vector3(24.9f, 1.22f, 19.2f), new Vector3(1.15f, 0.45f, 1.2f)) ||
            IsEllipsoidSubcell(center, new Vector3(30.2f, 1.55f, 15.8f), new Vector3(1.0f, 0.75f, 1.0f));
    }

    private static bool IsEllipsoidSubcell(Vector3 center, Vector3 ellipsoidCenter, Vector3 radius)
    {
        Vector3 delta = center - ellipsoidCenter;
        float normalized =
            ((delta.X * delta.X) / (radius.X * radius.X)) +
            ((delta.Y * delta.Y) / (radius.Y * radius.Y)) +
            ((delta.Z * delta.Z) / (radius.Z * radius.Z));
        return normalized <= 1f;
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
