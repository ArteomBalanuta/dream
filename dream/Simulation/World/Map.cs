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
    // Insertion-ordered with O(1) Remove: its order is the checksum and export order, as with the
    // List it replaced, but a removal no longer scans and shifts every solid cube.
    private readonly InsertionOrderedSet<Cube> _solidCubes = new();
    private readonly Dictionary<GeometryCellKey, GeometryCellRecord> _geometryCells = new();
    private readonly Dictionary<GeometryCellKey, IReadOnlyList<GeometryRenderBlockDescriptor>> _renderBlocksByCell = new();
    private readonly Dictionary<GeometryCellKey, IReadOnlyList<GeometryCollisionBodyDescriptor>> _collisionBodiesByCell = new();
    private readonly HashSet<GeometryCellKey> _staticPhysicsCells = [];
    private readonly HashSet<GeometryCellKey> _pendingRenderDirtyCells = [];
    private readonly HashSet<GeometryCellKey> _pendingStaticRenderDirtyCells = [];
    private readonly HashSet<GeometryCellKey> _staticRenderCells = [];
    private readonly HashSet<GeometryCellKey> _pendingCollisionDirtyCells = [];
    private IReadOnlyList<GeometryRenderBlockDescriptor> _mergedStaticSubcellRenderBlocks = [];
    private HashSet<GeometryCellKey> _mergedStaticSubcellRenderCells = [];
    private bool _mergedStaticSubcellRenderReady;
    private IReadOnlyList<GeometryCollisionBodyDescriptor> _mergedStaticSubcellCollisionBodies = [];
    private HashSet<GeometryCellKey> _mergedStaticSubcellCollisionCells = [];
    private bool _mergedStaticSubcellCollisionReady;
    private bool _initializing;
    private readonly WorldSpatialIndex _placedCubeSpatialIndex = new();
    private readonly Dictionary<Cube, GeometryCellKey> _cubeCells = new();
    // Fracture rubble is stored apart from cell occupancy, keyed by the cell it came from. It used
    // to live in the fractured cube's own cell record, so the cell stayed blocked for cubes and
    // placement forever even after the pieces rolled away, and a later cube could not coexist
    // with old rubble. Rubble has its own render/collision descriptors and physics bodies.
    private readonly Dictionary<GeometryCellKey, GeometryCellRecord> _rubbleCells = new();
    private readonly Dictionary<GeometryCellKey, IReadOnlyList<GeometryRenderBlockDescriptor>> _rubbleRenderBlocks = new();
    private readonly Dictionary<GeometryCellKey, IReadOnlyList<GeometryCollisionBodyDescriptor>> _rubbleCollisionBodies = new();
    // Server actor ids of replicated cubes, so deltas resolve a cube by identity rather than
    // by whatever cube currently sits in a cell.
    private readonly Dictionary<int, Cube> _cubesByReplicatedId = new();
    private readonly Dictionary<Cube, int> _replicatedIdsByCube = new();

    // Process-wide, so two maps (or a rebuilt map) never report the same static content version.
    private static long _staticContentVersionSource;

    // Cells whose render content is dynamic (the complement of _staticRenderCells among
    // _geometryCells), so the per-frame presentation export only visits those.
    private readonly HashSet<GeometryCellKey> _dynamicRenderCells = [];

    // Radius-independent static part of the presentation export, valid while
    // _cachedStaticRenderVersion == StaticContentVersion. Never mutated once built.
    private long _cachedStaticRenderVersion;
    private GeometryRenderCell[] _cachedStaticRenderCells = [];
    private Cube[] _cachedStaticCompatibilityCubes = [];
    private GeometryRenderBlockDescriptor[] _cachedStaticRenderBlocks = [];

    public int Width => _grid.Width;
    public int Height => _grid.Height;
    public int Depth => _grid.Depth;
    public Cube?[,,] Cubes => _grid.Cubes;
    public IReadOnlyList<Cube> SolidCubes => _solidCubes;
    public IReadOnlyDictionary<GeometryCellKey, GeometryCellRecord> GeometryCells => _geometryCells;

    // Changes whenever static render content may have changed; see GeometryRenderExport.StaticContentVersion.
    public long StaticContentVersion { get; private set; } = Interlocked.Increment(ref _staticContentVersionSource);

    // Rubble records (FracturePieces set), keyed by origin cell.
    public IReadOnlyDictionary<GeometryCellKey, GeometryCellRecord> RubbleCells => _rubbleCells;

    public GeometryCellRecord? TryGetRubbleAtCell(GeometryCellKey cell)
    {
        return _rubbleCells.TryGetValue(cell, out GeometryCellRecord? record) ? record : null;
    }

    public Map(
        PhysicsIntegrator? physics = null,
        bool generateStartupTerrain = false,
        int width = 25,
        int height = 8,
        int depth = 25,
        bool replicatedCubeBodies = false)
    {
        _physics = physics;
        _grid = new WorldGrid(width, height, depth);
        _generator = new WorldGenerator();
        _generateStartupTerrain = generateStartupTerrain;
        _physicsBinder = _physics != null ? new WorldPhysicsBinder(_physics, replicatedCubeBodies) : null;
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
        RebuildMergedStaticSubcellCollisionBodies(syncPhysics: false);
        _mergedStaticSubcellRenderReady = true;
        _mergedStaticSubcellCollisionReady = true;

        if (_physicsBinder != null)
        {
            GeometryCollisionExport collisionExport = CaptureGeometryCollisionExport();
            foreach (GeometryCollisionBodyDescriptor floorBody in collisionExport.Bodies.Where(body => body.Kind == GeometryCollisionBodyKind.Floor))
            {
                _physicsBinder.AddCollisionBody(floorBody);
            }

            _physicsBinder.SyncMergedStaticCollisionBodies(_mergedStaticSubcellCollisionBodies);
            foreach (IGrouping<GeometryCellKey, GeometryCollisionBodyDescriptor> cellGroup in collisionExport.Bodies
                .Where(body => body.Kind != GeometryCollisionBodyKind.Floor &&
                    body.CellKey.HasValue &&
                    !_mergedStaticSubcellCollisionCells.Contains(body.CellKey.Value))
                .GroupBy(body => body.CellKey!.Value))
            {
                _physicsBinder.SyncCellCollisionBodies(cellGroup.Key, cellGroup.ToArray());
            }

            // Supports of the startup cells (see NeedsCellSupport). The cell dictionary's order is
            // the same for every map built from the same terrain, so the bodies are added identically.
            foreach (GeometryCellKey key in _geometryCells.Keys)
            {
                if (NeedsCellSupport(key))
                {
                    _physicsBinder.SyncCellSupportBody(key, needed: true);
                }
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

    public bool AddCube(
        Vector3 position,
        bool isStatic = false,
        bool withPhysics = true,
        GeometryMaterialId? materialId = null)
    {
        return TryAddCube(position, isStatic, withPhysics, materialId) != null;
    }

    public Cube? TryAddCube(
        Vector3 position,
        bool isStatic = false,
        bool withPhysics = true,
        GeometryMaterialId? materialId = null)
    {
        int x = (int)MathF.Floor(position.X + 1e-6f);
        int y = (int)MathF.Floor(position.Y + 1e-6f);
        int z = (int)MathF.Floor(position.Z + 1e-6f);

        if (!IsInBounds(new Vector3(x, y, z)) || Cubes[x, y, z] != null)
        {
            return null;
        }

        GeometryCellRecord geometryCell = _generator.CreateGeometryCell(
            new Vector3(x, y, z),
            isFloor: y == 0,
            materialId ?? GeometryMaterialId.Grass);
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

        foreach (GeometryCellRecord rubble in _rubbleCells.Values)
        {
            target.RestoreFracturePieces(
                rubble.Key,
                rubble.Definition.MaterialId,
                rubble.FracturePieces!,
                withPhysics: target._physics != null);
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
        MarkRenderDirty(key);
        _pendingCollisionDirtyCells.Add(key);
        Cubes[x, y, z] = null;
        _solidCubes.Remove(cube);
        _cubeCells.Remove(cube);
        if (_replicatedIdsByCube.Remove(cube, out int replicatedId))
        {
            _cubesByReplicatedId.Remove(replicatedId);
        }

        _placedCubeSpatialIndex.Remove(cube);
        _physicsBinder?.RemoveCellCollisionBodies(key);
        _physicsBinder?.RemoveCompatibilityCubeBodies(cube);
        _physicsBinder?.SyncCellSupportBody(key, needed: false);
        return true;
    }

    public void AssignReplicatedCubeId(Cube cube, int actorId)
    {
        ArgumentNullException.ThrowIfNull(cube);
        if (_replicatedIdsByCube.TryGetValue(cube, out int previousId))
        {
            _cubesByReplicatedId.Remove(previousId);
        }

        if (_cubesByReplicatedId.TryGetValue(actorId, out Cube? previousCube) && !ReferenceEquals(previousCube, cube))
        {
            _replicatedIdsByCube.Remove(previousCube);
        }

        _cubesByReplicatedId[actorId] = cube;
        _replicatedIdsByCube[cube] = actorId;
    }

    public bool TryGetCubeByReplicatedId(int actorId, out Cube? cube)
    {
        if (_cubesByReplicatedId.TryGetValue(actorId, out cube) && _cubeCells.ContainsKey(cube))
        {
            return true;
        }

        cube = null;
        return false;
    }

    public bool TryGetReplicatedId(Cube cube, out int replicatedId)
    {
        ArgumentNullException.ThrowIfNull(cube);
        return _replicatedIdsByCube.TryGetValue(cube, out replicatedId);
    }

    public bool TryGetCellForCube(Cube cube, out GeometryCellKey key)
    {
        return _cubeCells.TryGetValue(cube, out key);
    }

    public void UpdateCubePosition(Cube cube, Vector3 position, bool syncPhysics = true)
    {
        UpdateCubePosition(cube, position, reservedTargetCells: null, syncPhysics);
    }

    internal bool UpdateCubePosition(
        Cube cube,
        Vector3 position,
        ISet<GeometryCellKey>? reservedTargetCells,
        bool syncPhysics = true)
    {
        if (!_cubeCells.TryGetValue(cube, out GeometryCellKey oldKey) ||
            !IsFinite(position))
        {
            return false;
        }

        if (cube.Position != position)
        {
            _checkpointMovedCubes?.Add(cube);
        }

        GeometryCellKey newKey = new(
            (int)MathF.Floor(position.X + 1e-6f),
            (int)MathF.Floor(position.Y + 1e-6f),
            (int)MathF.Floor(position.Z + 1e-6f));

        if (oldKey == newKey)
        {
            // A static cube moving inside its cell skips MarkRenderDirty when physics is not synced,
            // but its position is part of the static content.
            if (cube.Position != position && _staticRenderCells.Contains(oldKey))
            {
                BumpStaticContentVersion();
            }

            cube.Position = position;
            _placedCubeSpatialIndex.UpdatePosition(cube, position);
            if (syncPhysics && _physicsBinder != null)
            {
                // Replicated moves arrive every tick; moving the live body avoids disposing and
                // reallocating Bullet shape/motion state/body per cube per tick.
                bool sameCellBodyMoved = _physicsBinder.TryMoveCompatibilityCubeBody(cube, oldKey, newKey, position);
                RebuildGeometryCellArtifacts(oldKey, syncPhysics: !sameCellBodyMoved);
            }

            return true;
        }

        if (!IsInBounds(new Vector3(newKey.X, newKey.Y, newKey.Z)) ||
            !_geometryCells.TryGetValue(oldKey, out GeometryCellRecord? oldCell))
        {
            return false;
        }

        if (!CanTransitionCubeToCell(cube, newKey, reservedTargetCells))
        {
            return false;
        }

        cube.Position = position;
        _placedCubeSpatialIndex.UpdatePosition(cube, position);

        // A static cube leaving its cell becomes dynamic in the new one, which needs a new body;
        // every other synced move keeps and reindexes the live body.
        bool bodyMoved = syncPhysics &&
            _physicsBinder != null &&
            !_staticPhysicsCells.Contains(oldKey) &&
            _physicsBinder.TryMoveCompatibilityCubeBody(cube, oldKey, newKey, position);
        bool rebuildPhysics = syncPhysics && _physicsBinder != null && !bodyMoved;

        _geometryCells.Remove(oldKey);
        _renderBlocksByCell.Remove(oldKey, out IReadOnlyList<GeometryRenderBlockDescriptor>? movedRenderBlocks);
        _collisionBodiesByCell.Remove(oldKey, out IReadOnlyList<GeometryCollisionBodyDescriptor>? movedCollisionBodies);
        _staticPhysicsCells.Remove(oldKey);
        Cubes[oldKey.X, oldKey.Y, oldKey.Z] = null;
        // The vacated cell is no longer occupied, so it no longer supports anything; the moved
        // cube is dynamic in its new cell and gets no support there.
        _physicsBinder?.SyncCellSupportBody(oldKey, needed: false);
        RebuildGeometryCellArtifacts(oldKey, syncPhysics: rebuildPhysics);

        // The record is keyed by cell, so a cell change allocates a new one. The old cell's
        // descriptor lists move with it and are rewritten in place for the new cell.
        GeometryCellRecord movedCell = new(
            newKey,
            oldCell.Definition,
            oldCell.SubdivisionState,
            oldCell.FracturePieces);

        _geometryCells[newKey] = movedCell;
        _cubeCells[cube] = newKey;
        Cubes[newKey.X, newKey.Y, newKey.Z] = cube;
        if (!syncPhysics)
        {
            _physicsBinder?.ReindexCompatibilityCubeBody(cube, oldKey, newKey);
        }

        if (movedRenderBlocks != null)
        {
            _renderBlocksByCell.TryAdd(newKey, movedRenderBlocks);
        }

        if (movedCollisionBodies != null)
        {
            _collisionBodiesByCell.TryAdd(newKey, movedCollisionBodies);
        }

        RebuildGeometryCellArtifacts(newKey, syncPhysics: rebuildPhysics);
        reservedTargetCells?.Add(newKey);
        return true;
    }

    public bool UpdateFracturePiecePosition(GeometryCellKey cell, int pieceIndex, Vector3 center)
    {
        return UpdateFracturePiecePositions(cell, [(pieceIndex, center)]);
    }

    public bool UpdateFracturePiecePositions(
        GeometryCellKey cell,
        IReadOnlyList<(int PieceIndex, Vector3 Center)> updates)
    {
        return UpdateFracturePiecePositionsCore(cell, updates, acceptedPieceIndices: null);
    }

    public bool UpdateFracturePiecePositions(
        GeometryCellKey cell,
        IReadOnlyList<(int PieceIndex, Vector3 Center)> updates,
        out HashSet<int> acceptedPieceIndices)
    {
        acceptedPieceIndices = [];
        return UpdateFracturePiecePositionsCore(cell, updates, acceptedPieceIndices);
    }

    // Per-tick form: clears acceptedPieceIndices and fills it with the accepted piece indices, so
    // the caller can reuse one set across calls.
    public bool UpdateFracturePiecePositions(
        GeometryCellKey cell,
        IReadOnlyList<(int PieceIndex, Vector3 Center)> updates,
        HashSet<int> acceptedPieceIndices)
    {
        ArgumentNullException.ThrowIfNull(acceptedPieceIndices);
        acceptedPieceIndices.Clear();
        return UpdateFracturePiecePositionsCore(cell, updates, acceptedPieceIndices);
    }

    // Resting pieces (every center unchanged) cost nothing. A moved piece copies the piece list
    // once per call, because the record's list is published through RubbleCells and is never
    // mutated in place; the render and collision descriptors are then rewritten in place.
    private bool UpdateFracturePiecePositionsCore(
        GeometryCellKey cell,
        IReadOnlyList<(int PieceIndex, Vector3 Center)> updates,
        HashSet<int>? acceptedPieceIndices)
    {
        if (!_rubbleCells.TryGetValue(cell, out GeometryCellRecord? record) ||
            record.FracturePieces is not IReadOnlyList<GeometryFracturePiece> currentPieces ||
            updates.Count == 0)
        {
            return false;
        }

        GeometryFracturePiece[]? movedPieces = null;
        bool anyAccepted = false;
        // Indexed loop: enumerating through the interface would box the list's enumerator.
        for (int i = 0; i < updates.Count; i++)
        {
            (int pieceIndex, Vector3 center) = updates[i];
            if (pieceIndex < 0 || pieceIndex >= currentPieces.Count)
            {
                continue;
            }

            // A later update of the same piece compares against the earlier one, as before.
            GeometryFracturePiece piece = movedPieces != null ? movedPieces[pieceIndex] : currentPieces[pieceIndex];
            GeometryFracturePiece movedPiece = piece with { Center = center };
            if (!movedPiece.IsValid())
            {
                continue;
            }

            anyAccepted = true;
            acceptedPieceIndices?.Add(pieceIndex);
            if (piece.Center == center)
            {
                continue;
            }

            movedPieces ??= CopyPieces(currentPieces);
            movedPieces[pieceIndex] = movedPiece;
        }

        if (!anyAccepted)
        {
            return false;
        }

        if (movedPieces == null)
        {
            return true;
        }

        record.SetFracturePieces(movedPieces);
        RebuildRubbleArtifacts(cell, syncPhysics: false);
        return true;
    }

    private static GeometryFracturePiece[] CopyPieces(IReadOnlyList<GeometryFracturePiece> pieces)
    {
        GeometryFracturePiece[] copy = new GeometryFracturePiece[pieces.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = pieces[i];
        }

        return copy;
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

        GeometryMaterialId materialId = record.Definition.MaterialId;
        Cube? compatibilityCube = Cubes[cell.X, cell.Y, cell.Z];
        if (compatibilityCube == null || !RemoveCube(compatibilityCube))
        {
            _geometryCells.Remove(cell);
            RebuildGeometryCellArtifacts(cell, syncPhysics: _physicsBinder != null);
        }

        SetRubble(cell, materialId, CubeFracturePattern.CreateFirstPassRubble(cell), _physicsBinder != null);
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

    public bool RestoreFracturePieces(
        GeometryCellKey cell,
        GeometryMaterialId materialId,
        IReadOnlyList<GeometryFracturePiece> pieces,
        bool withPhysics = true)
    {
        if (cell.Y == 0 ||
            pieces.Count == 0 ||
            !IsInBounds(new Vector3(cell.X, cell.Y, cell.Z)))
        {
            return false;
        }

        GeometryFracturePiece[] validPieces = pieces
            .Where(GeometryFracturePiece.IsValid)
            .ToArray();
        if (validPieces.Length == 0)
        {
            return false;
        }

        // Restoring rubble no longer removes a cube in the origin cell: rubble does not occupy it.
        // (Persistence restore used to delete a legitimately placed cube here.)
        SetRubble(cell, materialId, validPieces, withPhysics && _physicsBinder != null);
        return true;
    }

    public bool RemoveRubble(GeometryCellKey cell)
    {
        if (!_rubbleCells.Remove(cell))
        {
            return false;
        }

        RebuildRubbleArtifacts(cell, syncPhysics: _physicsBinder != null);
        return true;
    }

    private void SetRubble(
        GeometryCellKey cell,
        GeometryMaterialId materialId,
        IReadOnlyList<GeometryFracturePiece> pieces,
        bool syncPhysics)
    {
        GeometryCellRecord rubble = _generator.CreateGeometryCell(
            new Vector3(cell.X, cell.Y, cell.Z),
            isFloor: false,
            materialId);
        GeometrySubdivisionState subdivision = rubble.EnsureSubdivisionState();
        int resolution = rubble.Definition.SubdivisionResolution;
        for (int x = 0; x < resolution; x++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int z = 0; z < resolution; z++)
                {
                    subdivision.SetChildSolid(new GeometrySubcellKey(x, y, z), isSolid: false);
                }
            }
        }

        rubble.SetFracturePieces(pieces);
        _rubbleCells[cell] = rubble;
        RebuildRubbleArtifacts(cell, syncPhysics);
    }

    private void RebuildRubbleArtifacts(GeometryCellKey cell, bool syncPhysics)
    {
        _checkpointDirtyRubbleCells?.Add(cell);
        _pendingRenderDirtyCells.Add(cell);
        _pendingCollisionDirtyCells.Add(cell);
        if (!_rubbleCells.TryGetValue(cell, out GeometryCellRecord? rubble))
        {
            _rubbleRenderBlocks.Remove(cell);
            _rubbleCollisionBodies.Remove(cell);
            if (syncPhysics)
            {
                _physicsBinder?.RemoveRubbleBodies(cell);
            }

            return;
        }

        _rubbleRenderBlocks.TryGetValue(cell, out IReadOnlyList<GeometryRenderBlockDescriptor>? previousBlocks);
        _rubbleCollisionBodies.TryGetValue(cell, out IReadOnlyList<GeometryCollisionBodyDescriptor>? previousBodies);
        _rubbleRenderBlocks[cell] = BuildRenderBlocks(rubble, previousBlocks);
        IReadOnlyList<GeometryCollisionBodyDescriptor> bodies = BuildCollisionBodies(rubble, previousBodies);
        _rubbleCollisionBodies[cell] = bodies;
        if (syncPhysics)
        {
            _physicsBinder?.SyncRubbleBodies(cell, bodies);
        }
    }

    public IReadOnlyList<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)>
        CaptureFracturePieces()
    {
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> pieces = [];
        CaptureFracturePiecesInto(pieces);
        return pieces;
    }

    // Appends every valid fracture piece, in CaptureFracturePieces order, without allocating
    // beyond the caller's list growth.
    public void CaptureFracturePiecesInto(
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        foreach (GeometryCellRecord record in _rubbleCells.Values)
        {
            AppendFracturePieces(record, pieces);
        }
    }

    // Appends the valid fracture pieces of one rubble cell; false when the cell has no rubble.
    public bool CaptureFracturePiecesInto(
        GeometryCellKey cell,
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (!_rubbleCells.TryGetValue(cell, out GeometryCellRecord? record))
        {
            return false;
        }

        AppendFracturePieces(record, pieces);
        return true;
    }

    private static void AppendFracturePieces(
        GeometryCellRecord record,
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> pieces)
    {
        if (record.FracturePieces == null)
        {
            return;
        }

        for (int index = 0; index < record.FracturePieces.Count; index++)
        {
            GeometryFracturePiece piece = record.FracturePieces[index];
            if (!piece.IsValid())
            {
                continue;
            }

            pieces.Add((record.Key, index, piece, record.Definition.MaterialId));
        }
    }

    // Persistence checkpoint change tracking (off until EnableCheckpointTracking; client maps never
    // turn it on). Every cube position change and every rubble change (set, removed, a piece moved)
    // is recorded, so an incremental checkpoint visits only those. HashSet adds reuse capacity, so
    // a moving world adds no per-tick allocation once the sets have grown.
    private HashSet<Cube>? _checkpointMovedCubes;
    private HashSet<GeometryCellKey>? _checkpointDirtyRubbleCells;

    public bool IsCheckpointTrackingEnabled => _checkpointMovedCubes != null;

    public void EnableCheckpointTracking()
    {
        _checkpointMovedCubes ??= new HashSet<Cube>(ReferenceEqualityComparer.Instance);
        _checkpointDirtyRubbleCells ??= [];
    }

    // Cubes whose position changed since ClearCheckpointChanges; empty while tracking is off.
    // Concrete sets (read-only by contract), so the capture enumerates them without boxing.
    internal HashSet<Cube> CheckpointMovedCubes => _checkpointMovedCubes ?? EmptyCubeSet;

    // Rubble cells changed since ClearCheckpointChanges; empty while tracking is off.
    internal HashSet<GeometryCellKey> CheckpointDirtyRubbleCells => _checkpointDirtyRubbleCells ?? EmptyCellSet;

    public void ClearCheckpointChanges()
    {
        _checkpointMovedCubes?.Clear();
        _checkpointDirtyRubbleCells?.Clear();
    }

    private static readonly HashSet<Cube> EmptyCubeSet = [];
    private static readonly HashSet<GeometryCellKey> EmptyCellSet = [];

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
        bool wasInitializing = _initializing;
        _initializing = true;
        try
        {
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
        }
        finally
        {
            _initializing = wasInitializing;
        }

        if (restoredCount > 0)
        {
            RebuildMergedStaticSubcellRenderBlocks();
            RebuildMergedStaticSubcellCollisionBodies(syncPhysics: _physicsBinder != null);
        }

        return restoredCount;
    }

    // Test oracle: radius-limited reference export that CapturePresentationGeometryRenderExport is checked against.
    public GeometryRenderExport CaptureGeometryRenderExport(Vector3 position, float radius)
    {
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
            StaticContentVersion = StaticContentVersion
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
            staticDirtyCells: staticDirtyCells);
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

        const float minimumHitDistance = 0.25f;
        Vector3 normalizedDirection = direction.Normalized();
        Cube? closest = null;
        float closestDistance = float.MaxValue;

        // Test the ray against each nearby cube's actual box. Sampling by integer cell missed the
        // visible part of a pushed, off-center cube and hit empty space in its cell instead.
        Vector3 midpoint = origin + (normalizedDirection * (maxDistance * 0.5f));
        foreach (Cube cube in _placedCubeSpatialIndex.GetNearby(midpoint, (maxDistance * 0.5f) + 1f))
        {
            if (!_cubeCells.TryGetValue(cube, out GeometryCellKey key) ||
                (_geometryCells.TryGetValue(key, out GeometryCellRecord? record) && record.SubdivisionState != null))
            {
                continue;
            }

            // Slightly inclusive so a ray running along a face (eye exactly on a cell boundary with
            // float residue in the facing) still hits, as the old cell sampling did.
            Vector3 halfSize = new((cube.Side * 0.5f) + 0.001f);
            if (TryIntersectRayWithBox(origin, normalizedDirection, cube.Position - halfSize, cube.Position + halfSize, out float enter, out float exit) &&
                exit >= minimumHitDistance &&
                enter <= maxDistance &&
                Math.Max(enter, 0f) < closestDistance)
            {
                closest = cube;
                closestDistance = Math.Max(enter, 0f);
            }
        }

        if (includeFloor)
        {
            for (float distance = minimumHitDistance; distance <= Math.Min(maxDistance, closestDistance); distance += 0.10f)
            {
                Vector3 cell = ToCellPosition(origin + (normalizedDirection * distance));
                if (cell.Y == 0 && TryGetCubeAtCell((int)cell.X, 0, (int)cell.Z) is Cube floor)
                {
                    return floor;
                }
            }
        }

        return closest;
    }

    private static bool TryIntersectRayWithBox(
        Vector3 origin,
        Vector3 direction,
        Vector3 min,
        Vector3 max,
        out float enter,
        out float exit)
    {
        enter = float.MinValue;
        exit = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = origin[axis];
            float d = direction[axis];
            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < min[axis] || o > max[axis])
                {
                    return false;
                }

                continue;
            }

            float t1 = (min[axis] - o) / d;
            float t2 = (max[axis] - o) / d;
            enter = Math.Max(enter, Math.Min(t1, t2));
            exit = Math.Min(exit, Math.Max(t1, t2));
            if (enter > exit)
            {
                return false;
            }
        }

        return true;
    }

    // Consistency checks across the map's parallel indexes. Mutations update several indexes
    // step by step; this makes a broken invariant visible (tests, and debug builds after
    // physics sync) instead of surfacing later as an invisible or duplicated cube.
    public IReadOnlyList<string> FindInvariantViolations()
    {
        List<string> violations = [];
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int z = 0; z < Depth; z++)
                {
                    if (Cubes[x, y, z] is not Cube cube)
                    {
                        continue;
                    }

                    GeometryCellKey key = new(x, y, z);
                    if (!_cubeCells.TryGetValue(cube, out GeometryCellKey indexed) || indexed != key)
                    {
                        violations.Add($"Cubes[{x},{y},{z}] is not indexed to its cell");
                    }

                    if (!_geometryCells.ContainsKey(key))
                    {
                        violations.Add($"Cubes[{x},{y},{z}] has no geometry record");
                    }
                }
            }
        }

        foreach ((Cube cube, GeometryCellKey key) in _cubeCells)
        {
            if (!ReferenceEquals(TryGetCubeAtCell(key.X, key.Y, key.Z), cube))
            {
                violations.Add($"cube indexed at {key} is not the occupant of that cell");
            }
        }

        if (_cubeCells.Count != _solidCubes.Count)
        {
            violations.Add($"solid cube list has {_solidCubes.Count} entries but {_cubeCells.Count} cubes are indexed");
        }

        foreach (GeometryCellRecord record in _geometryCells.Values)
        {
            if (record.FracturePieces != null)
            {
                violations.Add($"cell {record.Key} holds rubble; rubble belongs in the rubble store");
            }

            if (ShouldUseCoarseCompatibilityRender(record) && TryGetCubeAtCell(record.Key.X, record.Key.Y, record.Key.Z) == null)
            {
                violations.Add($"solid cell {record.Key} has no cube");
            }
        }

        foreach ((int id, Cube cube) in _cubesByReplicatedId)
        {
            if (!_replicatedIdsByCube.TryGetValue(cube, out int reverse) || reverse != id)
            {
                violations.Add($"replicated id {id} is not mapped back to its cube");
            }
        }

        return violations;
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
        bool wasMergedStaticSubcellCollisionCell = _mergedStaticSubcellCollisionCells.Contains(key);
        if (!_geometryCells.TryGetValue(key, out GeometryCellRecord? geometryCell))
        {
            _renderBlocksByCell.Remove(key);
            _collisionBodiesByCell.Remove(key);
            MarkRenderDirty(key);
            _pendingCollisionDirtyCells.Add(key);
            if (syncPhysics)
            {
                _physicsBinder?.RemoveCellCollisionBodies(key);
                _physicsBinder?.SyncCellSupportBody(key, needed: false);
            }

            RebuildMergedStaticSubcellRenderBlocksIfNeeded(wasMergedStaticSubcellRenderCell, needsMergedStaticSubcellRender: false);
            RebuildMergedStaticSubcellCollisionBodiesIfNeeded(
                wasMergedStaticSubcellCollisionCell,
                needsMergedStaticSubcellCollision: false,
                syncPhysics);
            return;
        }

        _renderBlocksByCell.TryGetValue(key, out IReadOnlyList<GeometryRenderBlockDescriptor>? previousRenderBlocks);
        _collisionBodiesByCell.TryGetValue(key, out IReadOnlyList<GeometryCollisionBodyDescriptor>? previousCollisionBodies);
        IReadOnlyList<GeometryRenderBlockDescriptor> renderBlocks = BuildRenderBlocks(geometryCell, previousRenderBlocks);
        IReadOnlyList<GeometryCollisionBodyDescriptor> collisionBodies = BuildCollisionBodies(geometryCell, previousCollisionBodies);

        _renderBlocksByCell[key] = renderBlocks;
        _collisionBodiesByCell[key] = collisionBodies;
        MarkRenderDirty(key);
        _pendingCollisionDirtyCells.Add(key);

        bool needsMergedStaticSubcellCollision = ShouldUseMergedStaticSubcellCollision(geometryCell);
        if (syncPhysics && geometryCell.Key.Y > 0 && !needsMergedStaticSubcellCollision)
        {
            _physicsBinder?.SyncCellCollisionBodies(key, collisionBodies);
        }

        if (syncPhysics)
        {
            _physicsBinder?.SyncCellSupportBody(key, NeedsCellSupport(key));
        }

        RebuildMergedStaticSubcellRenderBlocksIfNeeded(
            wasMergedStaticSubcellRenderCell,
            ShouldUseMergedSubcellRender(geometryCell));
        RebuildMergedStaticSubcellCollisionBodiesIfNeeded(
            wasMergedStaticSubcellCollisionCell,
            needsMergedStaticSubcellCollision,
            syncPhysics);
    }

    // previous is the cell's current descriptor list. Descriptor lists are owned by the map and
    // every export copies them, so the fracture and coarse-cube paths, which run on every
    // physics sync of a moving piece or cube, rewrite a previous array of the right length in
    // place instead of allocating a new one.
    private IReadOnlyList<GeometryRenderBlockDescriptor> BuildRenderBlocks(
        GeometryCellRecord record,
        IReadOnlyList<GeometryRenderBlockDescriptor>? previous)
    {
        if (record.FracturePieces is { Count: > 0 } pieces)
        {
            GeometryRenderBlockDescriptor[] blocks = ReuseOrAllocate(previous, CountValidPieces(pieces));
            int next = 0;
            for (int index = 0; index < pieces.Count; index++)
            {
                GeometryFracturePiece piece = pieces[index];
                if (!piece.IsValid())
                {
                    continue;
                }

                blocks[next++] = _generator.CreateRenderBlockDescriptor(
                    record,
                    piece.Center,
                    piece.SideLength);
            }

            return blocks;
        }

        if (ShouldUseCoarseCompatibilityRender(record))
        {
            Vector3 center = new(record.Key.X + 0.5f, record.Key.Y + 0.5f, record.Key.Z + 0.5f);
            GeometryRenderBlockDescriptor[] blocks = ReuseOrAllocate(previous, 1);
            blocks[0] = _generator.CreateRenderBlockDescriptor(record, center, 1f);
            return blocks;
        }

        if (ShouldUseMergedSubcellRender(record))
        {
            return BuildMergedSubcellRenderBlocks(record);
        }

        return BuildSubcellRenderBlocks(record);
    }

    // Kept apart from BuildRenderBlocks: the lambda's closure over record would otherwise be
    // allocated on entry to BuildRenderBlocks, on every rebuild of a coarse cube cell too.
    private IReadOnlyList<GeometryRenderBlockDescriptor> BuildSubcellRenderBlocks(GeometryCellRecord record)
    {
        return EnumerateSolidSubcells(record)
            .Select(entry => _generator.CreateRenderBlockDescriptor(
                record,
                entry.Center,
                entry.SideLength,
                entry.SubcellKey,
                ResolveSubcellVisualFillScale(record)))
            .ToArray();
    }

    // Same in-place reuse of previous as BuildRenderBlocks.
    private IReadOnlyList<GeometryCollisionBodyDescriptor> BuildCollisionBodies(
        GeometryCellRecord record,
        IReadOnlyList<GeometryCollisionBodyDescriptor>? previous)
    {
        if (record.Key.Y == 0)
        {
            return Array.Empty<GeometryCollisionBodyDescriptor>();
        }

        bool isStatic = _staticPhysicsCells.Contains(record.Key);
        if (record.FracturePieces is { Count: > 0 } pieces)
        {
            GeometryCollisionBodyDescriptor[] fractureBodies = ReuseOrAllocate(previous, CountValidPieces(pieces));
            int next = 0;
            for (int index = 0; index < pieces.Count; index++)
            {
                GeometryFracturePiece piece = pieces[index];
                if (!piece.IsValid())
                {
                    continue;
                }

                fractureBodies[next++] = new GeometryCollisionBodyDescriptor(
                    GeometryCollisionBodyKind.Subcell,
                    piece.Center,
                    new Vector3(piece.SideLength * 0.5f),
                    IsStatic: false,
                    CellKey: record.Key,
                    FracturePieceIndex: index);
            }

            return fractureBodies;
        }

        if (ShouldUseCoarseCompatibilityRender(record))
        {
            Cube? cube = Cubes[record.Key.X, record.Key.Y, record.Key.Z];
            if (cube == null)
            {
                return Array.Empty<GeometryCollisionBodyDescriptor>();
            }

            GeometryCollisionBodyDescriptor[] bodies = ReuseOrAllocate(previous, 1);
            bodies[0] = new GeometryCollisionBodyDescriptor(
                GeometryCollisionBodyKind.Cell,
                cube.Position,
                new Vector3(0.5f, 0.5f, 0.5f),
                isStatic,
                CellKey: record.Key,
                CompatibilityCube: cube);
            return bodies;
        }

        return BuildMergedSubcellCollisionBodies(record, isStatic);
    }

    private static T[] ReuseOrAllocate<T>(IReadOnlyList<T>? previous, int length)
    {
        if (length == 0)
        {
            return Array.Empty<T>();
        }

        return previous is T[] reusable && reusable.Length == length
            ? reusable
            : new T[length];
    }

    private static int CountValidPieces(IReadOnlyList<GeometryFracturePiece> pieces)
    {
        int count = 0;
        for (int index = 0; index < pieces.Count; index++)
        {
            if (pieces[index].IsValid())
            {
                count++;
            }
        }

        return count;
    }

    // Call after the cell's state has changed. Static dirtiness is tracked against what the cell
    // contributed before and after, so removing or converting static content still invalidates it.
    private void MarkRenderDirty(GeometryCellKey key)
    {
        _pendingRenderDirtyCells.Add(key);
        bool wasStatic = _staticRenderCells.Contains(key);
        bool isStatic = _geometryCells.TryGetValue(key, out GeometryCellRecord? record) &&
            IsStaticRenderContent(record);
        if (isStatic)
        {
            _staticRenderCells.Add(key);
            _dynamicRenderCells.Remove(key);
        }
        else
        {
            _staticRenderCells.Remove(key);
            if (record != null)
            {
                _dynamicRenderCells.Add(key);
            }
            else
            {
                _dynamicRenderCells.Remove(key);
            }
        }

        if (wasStatic || isStatic)
        {
            _pendingStaticRenderDirtyCells.Add(key);
            BumpStaticContentVersion();
        }
    }

    private void BumpStaticContentVersion()
    {
        StaticContentVersion = Interlocked.Increment(ref _staticContentVersionSource);
    }

    // Mirrors the static/dynamic split in CaptureGeometryRenderExport.
    private bool IsStaticRenderContent(GeometryCellRecord record)
    {
        return ShouldUseCoarseCompatibilityRender(record)
            ? IsStaticCompatibilityRender(record.Key)
            : record.FracturePieces == null;
    }

    private static bool ShouldUseCoarseCompatibilityRender(GeometryCellRecord record)
    {
        return record.SubdivisionState == null &&
            record.Definition.ShapeKind == GeometryShapeKind.SolidCube;
    }

    private bool CanTransitionCubeToCell(
        Cube cube,
        GeometryCellKey newKey,
        ISet<GeometryCellKey>? reservedTargetCells)
    {
        if (reservedTargetCells?.Contains(newKey) == true)
        {
            return false;
        }

        Cube? occupiedCube = Cubes[newKey.X, newKey.Y, newKey.Z];
        if (occupiedCube != null && !ReferenceEquals(occupiedCube, cube))
        {
            return false;
        }

        if (_geometryCells.TryGetValue(newKey, out GeometryCellRecord? record) &&
            !ReferenceEquals(occupiedCube, cube) &&
            record.SubdivisionState != null)
        {
            return false;
        }

        return true;
    }

    // A cell the map treats as fully occupied (CanTransitionCubeToCell rejects any other cube)
    // whose static collision is only its solid microcells needs a support, so dynamic cubes are
    // blocked by the whole cell (WorldPhysicsBinder.SyncCellSupportBody). Floor cells (y == 0)
    // sit inside the floor body, and fractured cells are left to their dynamic pieces.
    private bool NeedsCellSupport(GeometryCellKey key)
    {
        return key.Y > 0 &&
            _staticPhysicsCells.Contains(key) &&
            Cubes[key.X, key.Y, key.Z] != null &&
            _geometryCells.TryGetValue(key, out GeometryCellRecord? record) &&
            record.SubdivisionState != null &&
            record.FracturePieces is not { Count: > 0 };
    }

    // Test hook: whether the cell currently has a support body.
    internal bool HasCellSupportBody(GeometryCellKey key)
    {
        return _physicsBinder != null && _physicsBinder.TryGetCellSupportBody(key, out _);
    }

    internal int CellSupportBodyCount => _physicsBinder?.CellSupportBodyCount ?? 0;

    private bool IsStaticCompatibilityRender(GeometryCellKey key)
    {
        return _staticPhysicsCells.Contains(key) || key.Y == 0;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.X) &&
            !float.IsNaN(value.Y) &&
            !float.IsNaN(value.Z) &&
            !float.IsInfinity(value.X) &&
            !float.IsInfinity(value.Y) &&
            !float.IsInfinity(value.Z);
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

    private bool ShouldUseMergedStaticSubcellCollision(GeometryCellRecord record)
    {
        return ShouldUseMergedSubcellRender(record) && _staticPhysicsCells.Contains(record.Key);
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
