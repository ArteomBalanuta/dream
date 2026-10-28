using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

public partial class Map
{
    private void RebuildMergedStaticSubcellRenderBlocksIfNeeded(
        bool wasMergedStaticSubcellRenderCell,
        bool needsMergedStaticSubcellRender)
    {
        if (_initializing || !_mergedStaticSubcellRenderReady)
        {
            return;
        }

        if (wasMergedStaticSubcellRenderCell || needsMergedStaticSubcellRender)
        {
            RebuildMergedStaticSubcellRenderBlocks();
        }
    }

    private void RebuildMergedStaticSubcellRenderBlocks()
    {
        GeometryCellRecord[] records = _geometryCells.Values
            .Where(ShouldUseMergedSubcellRender)
            .ToArray();

        _mergedStaticSubcellRenderCells = records
            .Select(record => record.Key)
            .ToHashSet();

        List<GeometryRenderBlockDescriptor> blocks = [];
        foreach (IGrouping<GeometryMaterialId, GeometryCellRecord> materialGroup in records.GroupBy(record => record.Definition.MaterialId))
        {
            HashSet<WorldMicroCellKey> solidMicrocells = [];
            foreach (GeometryCellRecord record in materialGroup)
            {
                AddSolidMicrocells(record, solidMicrocells);
            }

            blocks.AddRange(BuildMergedGlobalMicrocellRenderBlocks(solidMicrocells));
        }

        _mergedStaticSubcellRenderBlocks = blocks;
        BumpStaticContentVersion();
    }

    private void RebuildMergedStaticSubcellCollisionBodiesIfNeeded(
        bool wasMergedStaticSubcellCollisionCell,
        bool needsMergedStaticSubcellCollision,
        bool syncPhysics)
    {
        if (_initializing || !_mergedStaticSubcellCollisionReady)
        {
            return;
        }

        if (wasMergedStaticSubcellCollisionCell || needsMergedStaticSubcellCollision)
        {
            RebuildMergedStaticSubcellCollisionBodies(syncPhysics);
        }
    }

    private void RebuildMergedStaticSubcellCollisionBodies(bool syncPhysics)
    {
        GeometryCellRecord[] records = _geometryCells.Values
            .Where(ShouldUseMergedStaticSubcellCollision)
            .ToArray();

        _mergedStaticSubcellCollisionCells = records
            .Select(record => record.Key)
            .ToHashSet();

        HashSet<WorldMicroCellKey> solidMicrocells = [];
        foreach (GeometryCellRecord record in records)
        {
            AddSolidMicrocells(record, solidMicrocells);
        }

        _mergedStaticSubcellCollisionBodies = BuildMergedGlobalMicrocellCollisionBodies(solidMicrocells);
        if (syncPhysics)
        {
            _physicsBinder?.SyncMergedStaticCollisionBodies(_mergedStaticSubcellCollisionBodies);
        }
    }

    private static void AddSolidMicrocells(
        GeometryCellRecord record,
        HashSet<WorldMicroCellKey> solidMicrocells)
    {
        int resolution = record.Definition.SubdivisionResolution;
        for (int y = 0; y < resolution; y++)
        {
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    GeometrySubcellKey subcellKey = new(x, y, z);
                    if (record.IsChildSolid(subcellKey))
                    {
                        solidMicrocells.Add(WorldMicroGrid.ToMicroCellKey(record.Key, subcellKey));
                    }
                }
            }
        }
    }

    private IReadOnlyList<GeometryRenderBlockDescriptor> BuildMergedGlobalMicrocellRenderBlocks(
        HashSet<WorldMicroCellKey> solidMicrocells)
    {
        HashSet<WorldMicroCellKey> visited = [];
        List<GeometryRenderBlockDescriptor> blocks = [];
        foreach (WorldMicroCellKey start in solidMicrocells
            .OrderBy(cell => cell.Y)
            .ThenBy(cell => cell.Z)
            .ThenBy(cell => cell.X))
        {
            if (visited.Contains(start))
            {
                continue;
            }

            WorldMicroCellKey max = ResolveMergedGlobalMicrocellBounds(solidMicrocells, visited, start);
            MarkVisitedGlobalMicrocells(visited, start, max);
            blocks.Add(CreateMergedGlobalRenderBlock(start, max));
        }

        return blocks;
    }

    private static WorldMicroCellKey ResolveMergedGlobalMicrocellBounds(
        HashSet<WorldMicroCellKey> solidMicrocells,
        HashSet<WorldMicroCellKey> visited,
        WorldMicroCellKey start)
    {
        int maxX = start.X;
        while (IsGlobalMergeCandidate(solidMicrocells, visited, new WorldMicroCellKey(maxX + 1, start.Y, start.Z)))
        {
            maxX++;
        }

        int maxZ = start.Z;
        while (CanMergeGlobalPlaneX(solidMicrocells, visited, start.X, maxX, start.Y, maxZ + 1))
        {
            maxZ++;
        }

        int maxY = start.Y;
        while (CanMergeGlobalLayerXZ(solidMicrocells, visited, start.X, maxX, maxY + 1, start.Z, maxZ))
        {
            maxY++;
        }

        return new WorldMicroCellKey(maxX, maxY, maxZ);
    }

    private static bool CanMergeGlobalPlaneX(
        HashSet<WorldMicroCellKey> solidMicrocells,
        HashSet<WorldMicroCellKey> visited,
        int minX,
        int maxX,
        int y,
        int z)
    {
        for (int x = minX; x <= maxX; x++)
        {
            if (!IsGlobalMergeCandidate(solidMicrocells, visited, new WorldMicroCellKey(x, y, z)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CanMergeGlobalLayerXZ(
        HashSet<WorldMicroCellKey> solidMicrocells,
        HashSet<WorldMicroCellKey> visited,
        int minX,
        int maxX,
        int y,
        int minZ,
        int maxZ)
    {
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!IsGlobalMergeCandidate(solidMicrocells, visited, new WorldMicroCellKey(x, y, z)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsGlobalMergeCandidate(
        HashSet<WorldMicroCellKey> solidMicrocells,
        HashSet<WorldMicroCellKey> visited,
        WorldMicroCellKey key)
    {
        return solidMicrocells.Contains(key) && !visited.Contains(key);
    }

    private static void MarkVisitedGlobalMicrocells(
        HashSet<WorldMicroCellKey> visited,
        WorldMicroCellKey start,
        WorldMicroCellKey max)
    {
        for (int y = start.Y; y <= max.Y; y++)
        {
            for (int z = start.Z; z <= max.Z; z++)
            {
                for (int x = start.X; x <= max.X; x++)
                {
                    visited.Add(new WorldMicroCellKey(x, y, z));
                }
            }
        }
    }

    private GeometryRenderBlockDescriptor CreateMergedGlobalRenderBlock(
        WorldMicroCellKey start,
        WorldMicroCellKey max)
    {
        Vector3 min = new(
            start.X * WorldMicroGrid.MicroCellSize,
            start.Y * WorldMicroGrid.MicroCellSize,
            start.Z * WorldMicroGrid.MicroCellSize);
        Vector3 maxBounds = new(
            (max.X + 1) * WorldMicroGrid.MicroCellSize,
            (max.Y + 1) * WorldMicroGrid.MicroCellSize,
            (max.Z + 1) * WorldMicroGrid.MicroCellSize);
        Vector3 size = maxBounds - min;
        GeometryCellKey parent = WorldMicroGrid.ToParentCellKey(start);
        GeometrySubcellKey? subcellKey = start == max
            ? WorldMicroGrid.ToLocalSubcellKey(start)
            : null;

        return _generator.CreateRenderBlockDescriptor(
            _geometryCells[parent],
            min + (size * 0.5f),
            WorldMicroGrid.MicroCellSize,
            subcellKey,
            StartupSculptureVisualFillScale,
            size);
    }

    private static IReadOnlyList<GeometryCollisionBodyDescriptor> BuildMergedGlobalMicrocellCollisionBodies(
        HashSet<WorldMicroCellKey> solidMicrocells)
    {
        List<GeometryCollisionBodyDescriptor> bodies = [];
        foreach (IGrouping<GeometryCellKey, WorldMicroCellKey> parentGroup in solidMicrocells
            .GroupBy(WorldMicroGrid.ToParentCellKey)
            .OrderBy(group => group.Key.Y)
            .ThenBy(group => group.Key.Z)
            .ThenBy(group => group.Key.X))
        {
            bodies.Add(CreateCoarseStaticCollisionBody(parentGroup.Key, parentGroup));
        }

        return bodies;
    }

    private static GeometryCollisionBodyDescriptor CreateCoarseStaticCollisionBody(
        GeometryCellKey parent,
        IEnumerable<WorldMicroCellKey> microcells)
    {
        WorldMicroCellKey[] cells = microcells.ToArray();
        int minX = cells.Min(cell => cell.X);
        int minY = cells.Min(cell => cell.Y);
        int minZ = cells.Min(cell => cell.Z);
        int maxX = cells.Max(cell => cell.X);
        int maxY = cells.Max(cell => cell.Y);
        int maxZ = cells.Max(cell => cell.Z);
        Vector3 min = new(
            minX * WorldMicroGrid.MicroCellSize,
            minY * WorldMicroGrid.MicroCellSize,
            minZ * WorldMicroGrid.MicroCellSize);
        Vector3 maxBounds = new(
            (maxX + 1) * WorldMicroGrid.MicroCellSize,
            (maxY + 1) * WorldMicroGrid.MicroCellSize,
            (maxZ + 1) * WorldMicroGrid.MicroCellSize);
        Vector3 size = maxBounds - min;
        GeometrySubcellKey? subcellKey = cells.Length == 1
            ? WorldMicroGrid.ToLocalSubcellKey(cells[0])
            : null;

        return new GeometryCollisionBodyDescriptor(
            GeometryCollisionBodyKind.Subcell,
            Center: min + (size * 0.5f),
            HalfExtents: size * 0.5f,
            IsStatic: true,
            CellKey: parent,
            SubcellKey: subcellKey);
    }

    private IReadOnlyList<GeometryRenderBlockDescriptor> BuildMergedSubcellRenderBlocks(GeometryCellRecord record)
    {
        int resolution = record.Definition.SubdivisionResolution;
        bool[,,] visited = new bool[resolution, resolution, resolution];
        List<GeometryRenderBlockDescriptor> blocks = [];

        for (int y = 0; y < resolution; y++)
        {
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    GeometrySubcellKey start = new(x, y, z);
                    if (visited[x, y, z] || !record.IsChildSolid(start))
                    {
                        continue;
                    }

                    (int MaxX, int MaxY, int MaxZ) bounds = ResolveMergedSubcellBounds(record, visited, start);
                    MarkVisited(visited, start, bounds);
                    blocks.Add(CreateMergedRenderBlock(record, start, bounds));
                }
            }
        }

        return blocks;
    }

    private static IReadOnlyList<GeometryCollisionBodyDescriptor> BuildMergedSubcellCollisionBodies(
        GeometryCellRecord record,
        bool isStatic)
    {
        int resolution = record.Definition.SubdivisionResolution;
        bool[,,] visited = new bool[resolution, resolution, resolution];
        List<GeometryCollisionBodyDescriptor> bodies = [];

        for (int y = 0; y < resolution; y++)
        {
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    GeometrySubcellKey start = new(x, y, z);
                    if (visited[x, y, z] || !record.IsChildSolid(start))
                    {
                        continue;
                    }

                    (int MaxX, int MaxY, int MaxZ) bounds = ResolveMergedSubcellBounds(record, visited, start);
                    MarkVisited(visited, start, bounds);
                    bodies.Add(CreateMergedCollisionBody(record, isStatic, start, bounds));
                }
            }
        }

        return bodies;
    }

    private static (int MaxX, int MaxY, int MaxZ) ResolveMergedSubcellBounds(
        GeometryCellRecord record,
        bool[,,] visited,
        GeometrySubcellKey start)
    {
        int resolution = record.Definition.SubdivisionResolution;
        int maxX = start.X;
        while (maxX + 1 < resolution && IsMergeCandidate(record, visited, maxX + 1, start.Y, start.Z))
        {
            maxX++;
        }

        int maxZ = start.Z;
        while (maxZ + 1 < resolution && CanMergePlaneX(record, visited, start.X, maxX, start.Y, maxZ + 1))
        {
            maxZ++;
        }

        int maxY = start.Y;
        while (maxY + 1 < resolution && CanMergeLayerXZ(record, visited, start.X, maxX, maxY + 1, start.Z, maxZ))
        {
            maxY++;
        }

        return (maxX, maxY, maxZ);
    }

    private static bool CanMergePlaneX(
        GeometryCellRecord record,
        bool[,,] visited,
        int minX,
        int maxX,
        int y,
        int z)
    {
        for (int x = minX; x <= maxX; x++)
        {
            if (!IsMergeCandidate(record, visited, x, y, z))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CanMergeLayerXZ(
        GeometryCellRecord record,
        bool[,,] visited,
        int minX,
        int maxX,
        int y,
        int minZ,
        int maxZ)
    {
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!IsMergeCandidate(record, visited, x, y, z))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsMergeCandidate(
        GeometryCellRecord record,
        bool[,,] visited,
        int x,
        int y,
        int z)
    {
        return !visited[x, y, z] && record.IsChildSolid(new GeometrySubcellKey(x, y, z));
    }

    private static void MarkVisited(
        bool[,,] visited,
        GeometrySubcellKey start,
        (int MaxX, int MaxY, int MaxZ) bounds)
    {
        for (int y = start.Y; y <= bounds.MaxY; y++)
        {
            for (int z = start.Z; z <= bounds.MaxZ; z++)
            {
                for (int x = start.X; x <= bounds.MaxX; x++)
                {
                    visited[x, y, z] = true;
                }
            }
        }
    }

    private static GeometryCollisionBodyDescriptor CreateMergedCollisionBody(
        GeometryCellRecord record,
        bool isStatic,
        GeometrySubcellKey start,
        (int MaxX, int MaxY, int MaxZ) bounds)
    {
        float sideLength = 1f / record.Definition.SubdivisionResolution;
        Vector3 cellOrigin = new(record.Key.X, record.Key.Y, record.Key.Z);
        Vector3 min = cellOrigin + new Vector3(
            start.X * sideLength,
            start.Y * sideLength,
            start.Z * sideLength);
        Vector3 max = cellOrigin + new Vector3(
            (bounds.MaxX + 1) * sideLength,
            (bounds.MaxY + 1) * sideLength,
            (bounds.MaxZ + 1) * sideLength);
        Vector3 size = max - min;
        GeometrySubcellKey? subcellKey = start.X == bounds.MaxX &&
            start.Y == bounds.MaxY &&
            start.Z == bounds.MaxZ
                ? start
                : null;

        return new GeometryCollisionBodyDescriptor(
            GeometryCollisionBodyKind.Subcell,
            Center: min + (size * 0.5f),
            HalfExtents: size * 0.5f,
            isStatic,
            CellKey: record.Key,
            SubcellKey: subcellKey);
    }

    private GeometryRenderBlockDescriptor CreateMergedRenderBlock(
        GeometryCellRecord record,
        GeometrySubcellKey start,
        (int MaxX, int MaxY, int MaxZ) bounds)
    {
        float sideLength = 1f / record.Definition.SubdivisionResolution;
        Vector3 cellOrigin = new(record.Key.X, record.Key.Y, record.Key.Z);
        Vector3 min = cellOrigin + new Vector3(
            start.X * sideLength,
            start.Y * sideLength,
            start.Z * sideLength);
        Vector3 max = cellOrigin + new Vector3(
            (bounds.MaxX + 1) * sideLength,
            (bounds.MaxY + 1) * sideLength,
            (bounds.MaxZ + 1) * sideLength);
        Vector3 size = max - min;
        GeometrySubcellKey? subcellKey = start.X == bounds.MaxX &&
            start.Y == bounds.MaxY &&
            start.Z == bounds.MaxZ
                ? start
                : null;

        return _generator.CreateRenderBlockDescriptor(
            record,
            min + (size * 0.5f),
            sideLength,
            subcellKey,
            ResolveSubcellVisualFillScale(record),
            size);
    }
}
