using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;


public class Map
{
    private const int StartupSculptureSubdivisionResolution = WorldMicroGrid.MicroCellsPerWorldCell;
    private const float StartupSculptureVisualFillScale = 1.01f;

    private readonly IGeometryMutationService _geometryMutations = new GeometryMutationService();
    private readonly WorldGrid _grid;
    private readonly WorldGenerator _generator;
    private readonly bool _generateStartupTerrain;
    private readonly WorldPhysicsBinder? _physicsBinder;
    private readonly PhysicsIntegrator? _physics;
    private readonly List<Cube> _solidCubes = new();
    private readonly Dictionary<GeometryCellKey, GeometryCellRecord> _geometryCells = new();
    private readonly Dictionary<GeometryCellKey, IReadOnlyList<GeometryRenderBlockDescriptor>> _renderBlocksByCell = new();
    private readonly Dictionary<GeometryCellKey, IReadOnlyList<GeometryCollisionBodyDescriptor>> _collisionBodiesByCell = new();
    private readonly HashSet<GeometryCellKey> _staticPhysicsCells = [];
    private readonly HashSet<GeometryCellKey> _pendingRenderDirtyCells = [];
    private readonly HashSet<GeometryCellKey> _pendingCollisionDirtyCells = [];
    private IReadOnlyList<GeometryRenderBlockDescriptor> _mergedStaticSubcellRenderBlocks = [];
    private HashSet<GeometryCellKey> _mergedStaticSubcellRenderCells = [];
    private bool _mergedStaticSubcellRenderReady;
    private bool _initializing;
    private readonly WorldSpatialIndex _placedCubeSpatialIndex = new();
    private readonly Dictionary<Cube, GeometryCellKey> _cubeCells = new();

    public int Width => _grid.Width;
    public int Height => _grid.Height;
    public int Depth => _grid.Depth;
    public Cube?[,,] Cubes => _grid.Cubes;
    public IReadOnlyList<Cube> SolidCubes => _solidCubes;
    public IReadOnlyDictionary<GeometryCellKey, GeometryCellRecord> GeometryCells => _geometryCells;

    public Map(
        PhysicsIntegrator? physics = null,
        bool loadVisualAssets = true,
        bool generateStartupTerrain = false,
        int width = 25,
        int height = 8,
        int depth = 25)
    {
        _physics = physics;
        _grid = new WorldGrid(width, height, depth);
        _generator = new WorldGenerator(loadVisualAssets);
        _generateStartupTerrain = generateStartupTerrain;
        _physicsBinder = _physics != null ? new WorldPhysicsBinder(_physics) : null;
        Initialize();
    }

    private void Initialize()
    {
        _initializing = true;
        foreach (GeometryCellRecord record in _generator.CreateStartupGeometry(Width, Height, Depth, _generateStartupTerrain))
        {
            TryAddGeometryCell(record, isStatic: true, withPhysics: false);
        }

        _initializing = false;
        RebuildMergedStaticSubcellRenderBlocks();
        _mergedStaticSubcellRenderReady = true;

        if (_physicsBinder != null)
        {
            foreach (GeometryCollisionBodyDescriptor body in CaptureGeometryCollisionExport().Bodies)
            {
                _physicsBinder.AddCollisionBody(body);
            }
        }

        for (int x = 0; x < Width; x++)
        {
            for (int z = 0; z < Depth; z++)
            {
                Cube? cube = Cubes[x, 0, z];
                if (cube == null || cube.Position != new Vector3(x + 0.5f, 0.5f, z + 0.5f))
                {
                    Debug.Fail($"Cube at [{x},0,{z}] misaligned! Position: {cube?.Position}");
                }
            }
        }
    }

    public bool AddCube(Vector3 position, bool isStatic = false, bool withPhysics = true)
    {
        return TryAddCube(position, isStatic, withPhysics) != null;
    }

    public Cube? TryAddCube(Vector3 position, bool isStatic = false, bool withPhysics = true)
    {
        int x = (int)MathF.Floor(position.X + 1e-6f);
        int y = (int)MathF.Floor(position.Y + 1e-6f);
        int z = (int)MathF.Floor(position.Z + 1e-6f);

        if (!IsInBounds(new Vector3(x, y, z)) || Cubes[x, y, z] != null)
        {
            return null;
        }

        GeometryCellRecord geometryCell = _generator.CreateGeometryCell(new Vector3(x, y, z), isFloor: y == 0);
        Cube cube = MaterializeGeometryCell(geometryCell, isStatic, withPhysics);
        return cube;
    }

    public void CopyPlacedCubesTo(Map target)
    {
        foreach (GeometryCellRecord geometryCell in _geometryCells.Values)
        {
            int x = geometryCell.Key.X;
            int y = geometryCell.Key.Y;
            int z = geometryCell.Key.Z;

            if (y == 0)
            {
                continue;
            }

            Vector3 cell = new Vector3(x, y, z);
            if (!target.IsInBounds(cell))
            {
                continue;
            }

            target.TryAddGeometryCell(geometryCell, isStatic: false, withPhysics: target._physics != null);
        }
    }

    public bool IsInBounds(Vector3 position)
    {
        return _grid.IsInBounds(position);
    }

    public bool RemoveCube(Cube cube)
    {
        if (!TryGetCellForCube(cube, out GeometryCellKey key) || key.Y == 0)
        {
            return false;
        }

        int x = key.X;
        int y = key.Y;
        int z = key.Z;
        _geometryCells.Remove(key);
        _renderBlocksByCell.Remove(key);
        _collisionBodiesByCell.Remove(key);
        _staticPhysicsCells.Remove(key);
        _pendingRenderDirtyCells.Add(key);
        _pendingCollisionDirtyCells.Add(key);
        Cubes[x, y, z] = null;
        _solidCubes.Remove(cube);
        _cubeCells.Remove(cube);
        _placedCubeSpatialIndex.Remove(cube);
        _physicsBinder?.RemoveCellCollisionBodies(key);
        return true;
    }

    public bool TryGetCellForCube(Cube cube, out GeometryCellKey key)
    {
        return _cubeCells.TryGetValue(cube, out key);
    }

    public void UpdateCubePosition(Cube cube, Vector3 position)
    {
        cube.Position = position;
        if (_cubeCells.TryGetValue(cube, out GeometryCellKey key) && key.Y > 0)
        {
            _placedCubeSpatialIndex.UpdatePosition(cube, position);
        }
    }

    public GeometryCellRecord? TryGetGeometryCellAtCell(int x, int y, int z)
    {
        GeometryCellKey key = new GeometryCellKey(x, y, z);
        return _geometryCells.TryGetValue(key, out GeometryCellRecord? record)
            ? record
            : null;
    }

    public GeometryMutationResult ApplyGeometrySubcellSolid(
        GeometryCellKey cell,
        GeometrySubcellKey child,
        bool isSolid)
    {
        if (!_geometryCells.TryGetValue(cell, out GeometryCellRecord? record))
        {
            return new GeometryMutationResult(
                ChangedCells: Array.Empty<GeometryCellKey>(),
                SubdivisionActivated: false,
                RenderDirtyCells: Array.Empty<GeometryCellKey>(),
                CollisionDirtyCells: Array.Empty<GeometryCellKey>(),
                RemovedCells: Array.Empty<GeometryCellKey>());
        }

        GeometryMutationResult result = _geometryMutations.SetChildSolid(record, child, isSolid);
        foreach (GeometryCellKey dirtyCell in result.ChangedCells)
        {
            RebuildGeometryCellArtifacts(dirtyCell, syncPhysics: _physicsBinder != null);
        }

        return result;
    }

    public bool FractureCubeIntoPieces(GeometryCellKey cell)
    {
        if (!_geometryCells.TryGetValue(cell, out GeometryCellRecord? record) || cell.Y == 0)
        {
            return false;
        }

        GeometrySubdivisionState subdivision = record.EnsureSubdivisionState();
        int resolution = record.Definition.SubdivisionResolution;
        for (int x = 0; x < resolution; x++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int z = 0; z < resolution; z++)
                {
                    GeometrySubcellKey subcell = new(x, y, z);
                    subdivision.SetChildSolid(subcell, isSolid: false);
                }
            }
        }

        record.SetFracturePieces(CubeFracturePattern.CreateFirstPassRubble(cell));
        RebuildGeometryCellArtifacts(cell, syncPhysics: _physicsBinder != null);
        return true;
    }

    public bool RestoreSolidCubeCell(GeometryCellKey cell)
    {
        if (!_geometryCells.TryGetValue(cell, out GeometryCellRecord? existingCell) || cell.Y == 0)
        {
            return false;
        }

        if (existingCell.SubdivisionState != null && existingCell.FracturePieces == null)
        {
            RebuildGeometryCellArtifacts(cell, syncPhysics: _physicsBinder != null);
            return true;
        }

        _geometryCells[cell] = _generator.CreateGeometryCell(
            new Vector3(cell.X, cell.Y, cell.Z),
            isFloor: false,
            existingCell.Definition.MaterialId);
        RebuildGeometryCellArtifacts(cell, syncPhysics: _physicsBinder != null);
        return true;
    }

    public bool RestoreGeneratedStartupCell(GeometryCellKey cell)
    {
        return RestoreGeneratedStartupCells([cell]) == 1;
    }

    public int RestoreGeneratedStartupCells(IEnumerable<GeometryCellKey> cells)
    {
        HashSet<GeometryCellKey> requestedCells = cells
            .Where(cell => cell.Y > 0 && IsInBounds(new Vector3(cell.X, cell.Y, cell.Z)))
            .ToHashSet();
        if (requestedCells.Count == 0)
        {
            return 0;
        }

        int restoredCount = 0;
        foreach (GeometryCellRecord generatedCell in _generator.CreateStartupGeometry(Width, Height, Depth, _generateStartupTerrain))
        {
            if (!requestedCells.Contains(generatedCell.Key))
            {
                continue;
            }

            if (_geometryCells.ContainsKey(generatedCell.Key))
            {
                _geometryCells[generatedCell.Key] = generatedCell;
                RebuildGeometryCellArtifacts(generatedCell.Key, syncPhysics: _physicsBinder != null);
                restoredCount++;
                continue;
            }

            if (TryAddGeometryCell(generatedCell, isStatic: true, withPhysics: _physicsBinder != null) != null)
            {
                restoredCount++;
            }
        }

        return restoredCount;
    }

    public GeometryChunkSnapshot CaptureGeometryChunkSnapshot()
    {
        return new GeometryChunkSnapshot(_geometryCells.Values.ToArray());
    }

    public GeometryRenderExport CaptureGeometryRenderExport(Vector3 position, float radius)
    {
        List<GeometryRenderCell> cells = new();
        List<GeometryRenderBlockDescriptor> blocks = new();
        List<Cube> compatibilityCubes = new();
        HashSet<GeometryCellKey> includedCells = [];

        // These cube views are still compatibility artifacts while the renderer
        // migrates toward consuming geometry-native exports directly.
        foreach (Cube cube in GetRenderCandidateCubes(position, radius))
        {
            if ((cube.Position - position).LengthSquared > (radius * radius))
            {
                continue;
            }

            if (!TryGetCellForCube(cube, out GeometryCellKey key) ||
                !_geometryCells.TryGetValue(key, out GeometryCellRecord? cell))
            {
                continue;
            }

            if (!includedCells.Add(key))
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
                foreach (GeometryRenderBlockDescriptor block in cellBlocks)
                {
                    if (!IsGeometryBlockWithinRadius(block, position, radius))
                    {
                        continue;
                    }

                    blocks.Add(block);
                }
            }
        }

        foreach (GeometryRenderBlockDescriptor block in _mergedStaticSubcellRenderBlocks)
        {
            if (!IsGeometryBlockWithinRadius(block, position, radius))
            {
                continue;
            }

            blocks.Add(block);
        }

        GeometryCellKey[] dirtyCells = _pendingRenderDirtyCells.ToArray();
        _pendingRenderDirtyCells.Clear();

        return new GeometryRenderExport(
            DirtyCells: dirtyCells,
            Cells: cells,
            Blocks: blocks,
            CompatibilityCubes: compatibilityCubes);
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

        foreach (IReadOnlyList<GeometryCollisionBodyDescriptor> cellBodies in _collisionBodiesByCell.Values)
        {
            bodies.AddRange(cellBodies);
        }

        GeometryCellKey[] dirtyCells = _pendingCollisionDirtyCells.ToArray();
        _pendingCollisionDirtyCells.Clear();

        return new GeometryCollisionExport(
            DirtyCells: dirtyCells,
            Bodies: bodies);
    }

    public Cube? TryGetCubeAtCell(int x, int y, int z)
    {
        if (!_grid.IsInBounds(new Vector3(x, y, z)))
        {
            return null;
        }

        return Cubes[x, y, z];
    }

    public Cube? TryGetFirstSolidCubeAlongRay(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        bool includeFloor = false)
    {
        if (direction == Vector3.Zero || maxDistance <= 0f)
        {
            return null;
        }

        Vector3 normalizedDirection = direction.Normalized();
        const float stepSize = 0.10f;

        for (float distance = 0.25f; distance <= maxDistance; distance += stepSize)
        {
            Vector3 sample = origin + (normalizedDirection * distance);
            Vector3 cell = ToCellPosition(sample);
            Cube? cube = TryGetCubeAtCell((int)cell.X, (int)cell.Y, (int)cell.Z);
            if (cube == null)
            {
                continue;
            }

            if (!includeFloor && cell.Y == 0)
            {
                continue;
            }

            return cube;
        }

        return null;
    }

    public IEnumerable<Cube> GetNearbySolidCubes(Vector3 position, float radius)
    {
        return GetRenderCandidateCubes(position, radius);
    }

    public IEnumerable<Cube> GetRenderCandidateCubes(Vector3 position, float radius)
    {
        foreach (Cube cube in GetNearbyFloorCubes(position, radius))
        {
            yield return cube;
        }

        foreach (Cube cube in GetNearbyPlacedCubes(position, radius))
        {
            yield return cube;
        }
    }

    private IEnumerable<Cube> GetNearbyPlacedCubes(Vector3 position, float radius)
    {
        return _placedCubeSpatialIndex.GetNearby(position, radius);
    }

    private static Vector3 ToCellPosition(Vector3 position)
    {
        return new Vector3(
            (int)MathF.Floor(position.X + 1e-6f),
            (int)MathF.Floor(position.Y + 1e-6f),
            (int)MathF.Floor(position.Z + 1e-6f));
    }

    private IEnumerable<Cube> GetNearbyFloorCubes(Vector3 position, float radius)
    {
        int minX = Math.Max(0, (int)MathF.Floor(position.X - radius));
        int maxX = Math.Min(Width - 1, (int)MathF.Ceiling(position.X + radius));
        int minZ = Math.Max(0, (int)MathF.Floor(position.Z - radius));
        int maxZ = Math.Min(Depth - 1, (int)MathF.Ceiling(position.Z + radius));

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                Cube? cube = Cubes[x, 0, z];
                if (cube != null)
                {
                    yield return cube;
                }
            }
        }
    }

    private Cube MaterializeGeometryCell(GeometryCellRecord geometryCell, bool isStatic, bool withPhysics)
    {
        Cube cube = _generator.CreateCompatibilityCube(geometryCell);
        int x = geometryCell.Key.X;
        int y = geometryCell.Key.Y;
        int z = geometryCell.Key.Z;
        Cubes[x, y, z] = cube;
        _geometryCells[geometryCell.Key] = geometryCell;
        _cubeCells[cube] = geometryCell.Key;
        _solidCubes.Add(cube);
        if (y > 0)
        {
            _placedCubeSpatialIndex.Add(cube);
        }

        if (isStatic)
        {
            _staticPhysicsCells.Add(geometryCell.Key);
        }
        else
        {
            _staticPhysicsCells.Remove(geometryCell.Key);
        }

        RebuildGeometryCellArtifacts(geometryCell.Key, syncPhysics: withPhysics && _physicsBinder != null);

        return cube;
    }

    private Cube? TryAddGeometryCell(GeometryCellRecord geometryCell, bool isStatic, bool withPhysics)
    {
        int x = geometryCell.Key.X;
        int y = geometryCell.Key.Y;
        int z = geometryCell.Key.Z;
        if (!IsInBounds(new Vector3(x, y, z)) || Cubes[x, y, z] != null)
        {
            return null;
        }

        return MaterializeGeometryCell(geometryCell, isStatic, withPhysics);
    }

    private void RebuildGeometryCellArtifacts(GeometryCellKey key, bool syncPhysics)
    {
        bool wasMergedStaticSubcellRenderCell = _mergedStaticSubcellRenderCells.Contains(key);
        if (!_geometryCells.TryGetValue(key, out GeometryCellRecord? geometryCell))
        {
            _renderBlocksByCell.Remove(key);
            _collisionBodiesByCell.Remove(key);
            _pendingRenderDirtyCells.Add(key);
            _pendingCollisionDirtyCells.Add(key);
            if (syncPhysics)
            {
                _physicsBinder?.RemoveCellCollisionBodies(key);
            }

            RebuildMergedStaticSubcellRenderBlocksIfNeeded(wasMergedStaticSubcellRenderCell, needsMergedStaticSubcellRender: false);
            return;
        }

        IReadOnlyList<GeometryRenderBlockDescriptor> renderBlocks = BuildRenderBlocks(geometryCell);
        IReadOnlyList<GeometryCollisionBodyDescriptor> collisionBodies = BuildCollisionBodies(geometryCell);

        _renderBlocksByCell[key] = renderBlocks;
        _collisionBodiesByCell[key] = collisionBodies;
        _pendingRenderDirtyCells.Add(key);
        _pendingCollisionDirtyCells.Add(key);

        if (syncPhysics && geometryCell.Key.Y > 0)
        {
            _physicsBinder?.SyncCellCollisionBodies(key, collisionBodies);
        }

        RebuildMergedStaticSubcellRenderBlocksIfNeeded(
            wasMergedStaticSubcellRenderCell,
            ShouldUseMergedSubcellRender(geometryCell));
    }

    private IReadOnlyList<GeometryRenderBlockDescriptor> BuildRenderBlocks(GeometryCellRecord record)
    {
        if (record.FracturePieces is { Count: > 0 })
        {
            return record.FracturePieces
                .Select(piece => _generator.CreateRenderBlockDescriptor(
                    record,
                    piece.Center,
                    piece.SideLength))
                .ToArray();
        }

        if (ShouldUseCoarseCompatibilityRender(record))
        {
            Vector3 center = new(record.Key.X + 0.5f, record.Key.Y + 0.5f, record.Key.Z + 0.5f);
            return [_generator.CreateRenderBlockDescriptor(record, center, 1f)];
        }

        if (ShouldUseMergedSubcellRender(record))
        {
            return BuildMergedSubcellRenderBlocks(record);
        }

        return EnumerateSolidSubcells(record)
            .Select(entry => _generator.CreateRenderBlockDescriptor(
                record,
                entry.Center,
                entry.SideLength,
                entry.SubcellKey,
                ResolveSubcellVisualFillScale(record)))
            .ToArray();
    }

    private IReadOnlyList<GeometryCollisionBodyDescriptor> BuildCollisionBodies(GeometryCellRecord record)
    {
        if (record.Key.Y == 0)
        {
            return Array.Empty<GeometryCollisionBodyDescriptor>();
        }

        bool isStatic = _staticPhysicsCells.Contains(record.Key);
        if (record.FracturePieces is { Count: > 0 })
        {
            return record.FracturePieces
                .Select(piece => new GeometryCollisionBodyDescriptor(
                    GeometryCollisionBodyKind.Subcell,
                    piece.Center,
                    new Vector3(piece.SideLength * 0.5f),
                    IsStatic: true,
                    CellKey: record.Key))
                .ToArray();
        }

        if (ShouldUseCoarseCompatibilityRender(record))
        {
            Cube? cube = Cubes[record.Key.X, record.Key.Y, record.Key.Z];
            if (cube == null)
            {
                return Array.Empty<GeometryCollisionBodyDescriptor>();
            }

            return
            [
                new GeometryCollisionBodyDescriptor(
                    GeometryCollisionBodyKind.Cell,
                    cube.Position,
                    new Vector3(0.5f, 0.5f, 0.5f),
                    isStatic,
                    CellKey: record.Key,
                    CompatibilityCube: cube)
            ];
        }

        return BuildMergedSubcellCollisionBodies(record, isStatic);
    }

    private static bool ShouldUseCoarseCompatibilityRender(GeometryCellRecord record)
    {
        return record.SubdivisionState == null &&
            record.Definition.ShapeKind == GeometryShapeKind.SolidCube;
    }

    private static float ResolveSubcellVisualFillScale(GeometryCellRecord record)
    {
        return record.SubdivisionState != null &&
            record.FracturePieces == null &&
            record.Definition.SubdivisionResolution == StartupSculptureSubdivisionResolution
                ? StartupSculptureVisualFillScale
                : 1f;
    }

    private static bool ShouldUseMergedSubcellRender(GeometryCellRecord record)
    {
        return record.SubdivisionState != null &&
            record.FracturePieces == null &&
            record.Definition.SubdivisionResolution == StartupSculptureSubdivisionResolution;
    }

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

    private IEnumerable<(GeometrySubcellKey SubcellKey, Vector3 Center, float SideLength)> EnumerateSolidSubcells(GeometryCellRecord record)
    {
        int resolution = record.Definition.SubdivisionResolution;
        float sideLength = 1f / resolution;
        Vector3 cellOrigin = new(record.Key.X, record.Key.Y, record.Key.Z);

        for (int x = 0; x < resolution; x++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int z = 0; z < resolution; z++)
                {
                    GeometrySubcellKey subcellKey = new(x, y, z);
                    if (!record.IsChildSolid(subcellKey))
                    {
                        continue;
                    }

                    Vector3 center = cellOrigin + new Vector3(
                        (x + 0.5f) * sideLength,
                        (y + 0.5f) * sideLength,
                        (z + 0.5f) * sideLength);
                    yield return (subcellKey, center, sideLength);
                }
            }
        }
    }
}
