using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

public partial class Map
{
    // Test oracle: radius-limited reference export that CapturePresentationGeometryRenderExport is checked against.
    public GeometryRenderExport CaptureGeometryRenderExport(Vector3 position, float radius)
    {
        ThrowIfHeadless();
        return CaptureGeometryRenderExport(
            GetRenderCandidateCubes(position, radius),
            includeStaticCube: cube => IsCubeWithinRadius(cube, position, radius),
            includeDynamicCube: cube => IsCubeWithinRadius(cube, position, radius),
            includeStaticBlock: block => IsGeometryBlockWithinRadius(block, position, radius),
            includeDynamicBlock: block => IsGeometryBlockWithinRadius(block, position, radius));
    }

    // Test oracle: unfiltered reference export over every solid cube.
    public GeometryRenderExport CaptureFullGeometryRenderExport()
    {
        ThrowIfHeadless();
        return CaptureGeometryRenderExport(
            _solidCubes,
            includeStaticCube: _ => true,
            includeDynamicCube: _ => true,
            includeStaticBlock: _ => true,
            includeDynamicBlock: _ => true);
    }

    // Same content as CaptureGeometryRenderExport over every solid cube with static content always
    // included and dynamic content limited to the radius. The static part is cached per
    // StaticContentVersion; only dynamic cells and rubble are visited each frame.
    public GeometryRenderExport CapturePresentationGeometryRenderExport(Vector3 position, float radius)
    {
        ThrowIfHeadless();
        if (_cachedStaticRenderVersion != StaticContentVersion)
        {
            RebuildStaticPresentationCache();
        }

        List<GeometryRenderCell>? dynamicCells = null;
        List<Cube>? dynamicCompatibilityCubes = null;
        List<GeometryRenderBlockDescriptor>? dynamicBlocks = null;
        foreach (GeometryCellKey key in _dynamicRenderCells)
        {
            if (!_geometryCells.TryGetValue(key, out GeometryCellRecord? cell) ||
                IsStaticRenderContent(cell) ||
                Cubes[key.X, key.Y, key.Z] is not Cube cube ||
                !_cubeCells.TryGetValue(cube, out GeometryCellKey cubeKey) ||
                cubeKey != key)
            {
                continue;
            }

            if (ShouldUseCoarseCompatibilityRender(cell))
            {
                if (!IsCubeWithinRadius(cube, position, radius))
                {
                    continue;
                }

                (dynamicCells ??= []).Add(new GeometryRenderCell(key, cell, cube));
                (dynamicCompatibilityCubes ??= []).Add(cube);
                continue;
            }

            (dynamicCells ??= []).Add(new GeometryRenderCell(key, cell, cube));
            if (_mergedStaticSubcellRenderCells.Contains(key) ||
                !_renderBlocksByCell.TryGetValue(key, out IReadOnlyList<GeometryRenderBlockDescriptor>? cellBlocks))
            {
                continue;
            }

            AddBlocksWithinRadius(cellBlocks, position, radius, ref dynamicBlocks);
        }

        foreach (IReadOnlyList<GeometryRenderBlockDescriptor> rubbleBlocks in _rubbleRenderBlocks.Values)
        {
            AddBlocksWithinRadius(rubbleBlocks, position, radius, ref dynamicBlocks);
        }

        GeometryCellKey[] dirtyCells = DrainCells(_pendingRenderDirtyCells);
        GeometryCellKey[] staticDirtyCells = DrainCells(_pendingStaticRenderDirtyCells);
        IReadOnlyList<GeometryRenderCell> cells = dynamicCells == null
            ? _cachedStaticRenderCells
            : new ConcatenatedReadOnlyList<GeometryRenderCell>(_cachedStaticRenderCells, dynamicCells);

        return new GeometryRenderExport(
            DirtyCells: dirtyCells,
            Cells: cells,
            Blocks: _cachedStaticRenderBlocks,
            CompatibilityCubes: _cachedStaticCompatibilityCubes,
            dynamicCompatibilityCubes: dynamicCompatibilityCubes,
            dynamicBlocks: dynamicBlocks,
            staticDirtyCells: staticDirtyCells)
        {
            StaticContentVersion = StaticContentVersion,
            StaticCells = _cachedStaticRenderCells
        };
    }

    // The static half of CaptureGeometryRenderExport with every static cube and block included.
    private void RebuildStaticPresentationCache()
    {
        List<GeometryRenderCell> cells = [];
        List<Cube> compatibilityCubes = [];
        List<GeometryRenderBlockDescriptor> blocks = [];
        HashSet<GeometryCellKey> includedCells = [];
        foreach (Cube cube in _solidCubes)
        {
            if (!TryGetCellForCube(cube, out GeometryCellKey key) ||
                !_geometryCells.TryGetValue(key, out GeometryCellRecord? cell) ||
                !IsStaticRenderContent(cell) ||
                !includedCells.Add(key))
            {
                continue;
            }

            cells.Add(new GeometryRenderCell(key, cell, cube));
            if (ShouldUseCoarseCompatibilityRender(cell))
            {
                compatibilityCubes.Add(cube);
                continue;
            }

            if (_mergedStaticSubcellRenderCells.Contains(key))
            {
                continue;
            }

            if (_renderBlocksByCell.TryGetValue(key, out IReadOnlyList<GeometryRenderBlockDescriptor>? cellBlocks))
            {
                blocks.AddRange(cellBlocks);
            }
        }

        blocks.AddRange(_mergedStaticSubcellRenderBlocks);
        _cachedStaticRenderCells = cells.ToArray();
        _cachedStaticCompatibilityCubes = compatibilityCubes.ToArray();
        _cachedStaticRenderBlocks = blocks.ToArray();
        _cachedStaticRenderVersion = StaticContentVersion;
    }

    private void ThrowIfHeadless()
    {
        if (IsHeadless)
        {
            throw new InvalidOperationException("A headless map keeps no render artifacts; render exports need a map created with headless: false.");
        }
    }

    private static void AddBlocksWithinRadius(
        IReadOnlyList<GeometryRenderBlockDescriptor> source,
        Vector3 position,
        float radius,
        ref List<GeometryRenderBlockDescriptor>? destination)
    {
        // Indexed loop: enumerating through the interface would allocate an enumerator per cell.
        for (int index = 0; index < source.Count; index++)
        {
            GeometryRenderBlockDescriptor block = source[index];
            if (IsGeometryBlockWithinRadius(block, position, radius))
            {
                (destination ??= []).Add(block);
            }
        }
    }

    private static GeometryCellKey[] DrainCells(HashSet<GeometryCellKey> cells)
    {
        if (cells.Count == 0)
        {
            return Array.Empty<GeometryCellKey>();
        }

        GeometryCellKey[] drained = cells.ToArray();
        cells.Clear();
        return drained;
    }

    private GeometryRenderExport CaptureGeometryRenderExport(
        IEnumerable<Cube> cubeCandidates,
        Func<Cube, bool> includeStaticCube,
        Func<Cube, bool> includeDynamicCube,
        Func<GeometryRenderBlockDescriptor, bool> includeStaticBlock,
        Func<GeometryRenderBlockDescriptor, bool> includeDynamicBlock)
    {
        List<GeometryRenderCell> cells = new();
        List<GeometryRenderCell> staticCells = new();
        List<GeometryRenderBlockDescriptor> blocks = new();
        List<GeometryRenderBlockDescriptor> dynamicBlocks = new();
        List<Cube> compatibilityCubes = new();
        List<Cube> dynamicCompatibilityCubes = new();
        HashSet<GeometryCellKey> includedCells = [];

        // These cube views are still compatibility artifacts while the renderer
        // migrates toward consuming geometry-native exports directly.
        foreach (Cube cube in cubeCandidates)
        {
            if (!TryGetCellForCube(cube, out GeometryCellKey key) ||
                !_geometryCells.TryGetValue(key, out GeometryCellRecord? cell))
            {
                continue;
            }

            bool usesCoarseCompatibilityRender = ShouldUseCoarseCompatibilityRender(cell);
            if (usesCoarseCompatibilityRender)
            {
                bool isStaticCompatibility = IsStaticCompatibilityRender(key);
                bool shouldIncludeCube = isStaticCompatibility
                    ? includeStaticCube(cube)
                    : includeDynamicCube(cube);
                if (!shouldIncludeCube)
                {
                    continue;
                }
            }

            if (!includedCells.Add(key))
            {
                continue;
            }

            cells.Add(new GeometryRenderCell(key, cell, cube));
            if (IsStaticRenderContent(cell))
            {
                staticCells.Add(new GeometryRenderCell(key, cell, cube));
            }

            if (usesCoarseCompatibilityRender)
            {
                if (IsStaticCompatibilityRender(key))
                {
                    compatibilityCubes.Add(cube);
                }
                else
                {
                    dynamicCompatibilityCubes.Add(cube);
                }

                continue;
            }

            if (_mergedStaticSubcellRenderCells.Contains(key))
            {
                continue;
            }

            if (_renderBlocksByCell.TryGetValue(key, out IReadOnlyList<GeometryRenderBlockDescriptor>? cellBlocks))
            {
                Func<GeometryRenderBlockDescriptor, bool> shouldIncludeBlock =
                    cell.FracturePieces != null ? includeDynamicBlock : includeStaticBlock;
                foreach (GeometryRenderBlockDescriptor block in cellBlocks)
                {
                    if (!shouldIncludeBlock(block))
                    {
                        continue;
                    }

                    if (cell.FracturePieces != null)
                    {
                        dynamicBlocks.Add(block);
                    }
                    else
                    {
                        blocks.Add(block);
                    }
                }
            }
        }

        foreach (IReadOnlyList<GeometryRenderBlockDescriptor> rubbleBlocks in _rubbleRenderBlocks.Values)
        {
            foreach (GeometryRenderBlockDescriptor block in rubbleBlocks)
            {
                if (includeDynamicBlock(block))
                {
                    dynamicBlocks.Add(block);
                }
            }
        }

        foreach (GeometryRenderBlockDescriptor block in _mergedStaticSubcellRenderBlocks)
        {
            if (!includeStaticBlock(block))
            {
                continue;
            }

            blocks.Add(block);
        }

        GeometryCellKey[] dirtyCells = _pendingRenderDirtyCells.ToArray();
        GeometryCellKey[] staticDirtyCells = _pendingStaticRenderDirtyCells.ToArray();
        _pendingRenderDirtyCells.Clear();
        _pendingStaticRenderDirtyCells.Clear();

        return new GeometryRenderExport(
            DirtyCells: dirtyCells,
            Cells: cells,
            Blocks: blocks,
            CompatibilityCubes: compatibilityCubes,
            dynamicCompatibilityCubes: dynamicCompatibilityCubes,
            dynamicBlocks: dynamicBlocks,
            staticDirtyCells: staticDirtyCells)
        {
            StaticCells = staticCells
        };
    }

    private static bool IsGeometryBlockWithinRadius(
        GeometryRenderBlockDescriptor block,
        Vector3 position,
        float radius)
    {
        Vector3 halfSize = block.Size * 0.5f;
        Vector3 min = block.Center - halfSize;
        Vector3 max = block.Center + halfSize;
        float distanceSquared = SquaredDistanceToAabb(position, min, max);
        return distanceSquared <= radius * radius;
    }

    private static bool IsCubeWithinRadius(Cube cube, Vector3 position, float radius)
    {
        Vector3 halfSize = new(cube.Side * 0.5f);
        Vector3 min = cube.Position - halfSize;
        Vector3 max = cube.Position + halfSize;
        float distanceSquared = SquaredDistanceToAabb(position, min, max);
        return distanceSquared <= radius * radius;
    }

    private static float SquaredDistanceToAabb(Vector3 point, Vector3 min, Vector3 max)
    {
        float dx = DistanceToRange(point.X, min.X, max.X);
        float dy = DistanceToRange(point.Y, min.Y, max.Y);
        float dz = DistanceToRange(point.Z, min.Z, max.Z);
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static float DistanceToRange(float value, float min, float max)
    {
        if (value < min)
        {
            return min - value;
        }

        return value > max ? value - max : 0f;
    }

    public GeometryCollisionExport CaptureGeometryCollisionExport()
    {
        List<GeometryCollisionBodyDescriptor> bodies = new()
        {
            new GeometryCollisionBodyDescriptor(
                GeometryCollisionBodyKind.Floor,
                new Vector3(Width / 2f, 0.5f, Depth / 2f),
                new Vector3(Width / 2f, 0.5f, Depth / 2f),
                IsStatic: true)
        };

        foreach ((GeometryCellKey key, IReadOnlyList<GeometryCollisionBodyDescriptor> cellBodies) in _collisionBodiesByCell)
        {
            if (_mergedStaticSubcellCollisionCells.Contains(key))
            {
                continue;
            }

            bodies.AddRange(cellBodies);
        }

        bodies.AddRange(_mergedStaticSubcellCollisionBodies);
        foreach (IReadOnlyList<GeometryCollisionBodyDescriptor> rubbleBodies in _rubbleCollisionBodies.Values)
        {
            bodies.AddRange(rubbleBodies);
        }

        GeometryCellKey[] dirtyCells = _pendingCollisionDirtyCells.ToArray();
        _pendingCollisionDirtyCells.Clear();

        return new GeometryCollisionExport(
            DirtyCells: dirtyCells,
            Bodies: bodies);
    }
}
