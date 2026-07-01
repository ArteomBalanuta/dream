using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;


public class Map
{
    private readonly IGeometryMutationService _geometryMutations = new GeometryMutationService();
    private readonly WorldGrid _grid;
    private readonly WorldGenerator _generator;
    private readonly WorldPhysicsBinder? _physicsBinder;
    private readonly PhysicsIntegrator? _physics;
    private readonly List<Cube> _solidCubes = new();
    private readonly Dictionary<GeometryCellKey, GeometryCellRecord> _geometryCells = new();
    private readonly Dictionary<GeometryCellKey, IReadOnlyList<GeometryRenderBlockDescriptor>> _renderBlocksByCell = new();
    private readonly Dictionary<GeometryCellKey, IReadOnlyList<GeometryCollisionBodyDescriptor>> _collisionBodiesByCell = new();
    private readonly HashSet<GeometryCellKey> _staticPhysicsCells = [];
    private readonly HashSet<GeometryCellKey> _pendingRenderDirtyCells = [];
    private readonly HashSet<GeometryCellKey> _pendingCollisionDirtyCells = [];
    private readonly WorldSpatialIndex _placedCubeSpatialIndex = new();

    public int Width => _grid.Width;
    public int Height => _grid.Height;
    public int Depth => _grid.Depth;
    public Cube?[,,] Cubes => _grid.Cubes;
    public IReadOnlyList<Cube> SolidCubes => _solidCubes;
    public IReadOnlyDictionary<GeometryCellKey, GeometryCellRecord> GeometryCells => _geometryCells;

    public Map(
        PhysicsIntegrator? physics = null,
        bool loadVisualAssets = true,
        int width = 25,
        int height = 8,
        int depth = 25)
    {
        _physics = physics;
        _grid = new WorldGrid(width, height, depth);
        _generator = new WorldGenerator(loadVisualAssets);
        _physicsBinder = _physics != null ? new WorldPhysicsBinder(_physics) : null;
        Initialize();
    }

    private void Initialize()
    {
        for (int x = 0; x < Width; x++)
        {
            for (int z = 0; z < Depth; z++)
            {
                AddCube(new Vector3(x, 0, z), isStatic: true, withPhysics: false);
            }
        }

        if (_physicsBinder != null)
        {
            foreach (GeometryCollisionBodyDescriptor body in CaptureGeometryCollisionExport().Bodies)
            {
                if (body.Kind != GeometryCollisionBodyKind.Floor)
                {
                    continue;
                }

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
        Vector3 cell = ToCellPosition(cube.Position);
        int x = (int)cell.X;
        int y = (int)cell.Y;
        int z = (int)cell.Z;

        if (!IsInBounds(cell) || y == 0 || Cubes[x, y, z] != cube)
        {
            return false;
        }

        GeometryCellKey key = new GeometryCellKey(x, y, z);
        _geometryCells.Remove(key);
        _renderBlocksByCell.Remove(key);
        _collisionBodiesByCell.Remove(key);
        _staticPhysicsCells.Remove(key);
        _pendingRenderDirtyCells.Add(key);
        _pendingCollisionDirtyCells.Add(key);
        Cubes[x, y, z] = null;
        _solidCubes.Remove(cube);
        _placedCubeSpatialIndex.Remove(cube);
        _physicsBinder?.RemoveCellCollisionBodies(key);
        return true;
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

        IReadOnlySet<GeometrySubcellKey> rubblePieces = CubeFracturePattern.CreateFirstPassRubble(
            record.Definition.SubdivisionResolution);
        GeometrySubdivisionState subdivision = record.EnsureSubdivisionState();
        int resolution = record.Definition.SubdivisionResolution;
        for (int x = 0; x < resolution; x++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int z = 0; z < resolution; z++)
                {
                    GeometrySubcellKey subcell = new(x, y, z);
                    subdivision.SetChildSolid(subcell, rubblePieces.Contains(subcell));
                }
            }
        }

        RebuildGeometryCellArtifacts(cell, syncPhysics: _physicsBinder != null);
        return true;
    }

    public bool RestoreSolidCubeCell(GeometryCellKey cell)
    {
        if (!_geometryCells.ContainsKey(cell) || cell.Y == 0)
        {
            return false;
        }

        _geometryCells[cell] = _generator.CreateGeometryCell(new Vector3(cell.X, cell.Y, cell.Z), isFloor: false);
        RebuildGeometryCellArtifacts(cell, syncPhysics: _physicsBinder != null);
        return true;
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

            GeometryCellKey key = new GeometryCellKey(
                (int)MathF.Floor(cube.Position.X + 1e-6f),
                (int)MathF.Floor(cube.Position.Y + 1e-6f),
                (int)MathF.Floor(cube.Position.Z + 1e-6f));

            if (!_geometryCells.TryGetValue(key, out GeometryCellRecord? cell))
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

            if (_renderBlocksByCell.TryGetValue(key, out IReadOnlyList<GeometryRenderBlockDescriptor>? cellBlocks))
            {
                foreach (GeometryRenderBlockDescriptor block in cellBlocks)
                {
                    if ((block.Center - position).LengthSquared > (radius * radius))
                    {
                        continue;
                    }

                    blocks.Add(block);
                }
            }
        }

        GeometryCellKey[] dirtyCells = _pendingRenderDirtyCells.ToArray();
        _pendingRenderDirtyCells.Clear();

        return new GeometryRenderExport(
            DirtyCells: dirtyCells,
            Cells: cells,
            Blocks: blocks,
            CompatibilityCubes: compatibilityCubes);
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
    }

    private IReadOnlyList<GeometryRenderBlockDescriptor> BuildRenderBlocks(GeometryCellRecord record)
    {
        if (ShouldUseCoarseCompatibilityRender(record))
        {
            Vector3 center = new(record.Key.X + 0.5f, record.Key.Y + 0.5f, record.Key.Z + 0.5f);
            return [_generator.CreateRenderBlockDescriptor(record, center, 1f)];
        }

        return EnumerateSolidSubcells(record)
            .Select(entry => _generator.CreateRenderBlockDescriptor(
                record,
                entry.Center,
                entry.SideLength,
                entry.SubcellKey))
            .ToArray();
    }

    private IReadOnlyList<GeometryCollisionBodyDescriptor> BuildCollisionBodies(GeometryCellRecord record)
    {
        if (record.Key.Y == 0)
        {
            return Array.Empty<GeometryCollisionBodyDescriptor>();
        }

        bool isStatic = _staticPhysicsCells.Contains(record.Key);
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

        return EnumerateSolidSubcells(record)
            .Select(entry => new GeometryCollisionBodyDescriptor(
                GeometryCollisionBodyKind.Subcell,
                entry.Center,
                new Vector3(entry.SideLength * 0.5f),
                isStatic,
                CellKey: record.Key,
                SubcellKey: entry.SubcellKey))
            .ToArray();
    }

    private static bool ShouldUseCoarseCompatibilityRender(GeometryCellRecord record)
    {
        return record.SubdivisionState == null &&
            record.Definition.ShapeKind == GeometryShapeKind.SolidCube;
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
