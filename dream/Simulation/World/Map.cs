using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;


public partial class Map
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

    // A headless map (the authoritative server's) builds and keeps no render artifacts: no render
    // block descriptors per cell or per rubble cell, no merged static sculpture render blocks, no
    // render-dirty or static/dynamic render cell sets, and StaticContentVersion never changes.
    // Geometry records, collision descriptors, physics bodies, replicated ids and the world
    // checksum are maintained exactly as on a rendering map. The render exports throw on it.
    public bool IsHeadless { get; }

    // Rubble records (FracturePieces set), keyed by origin cell.
    public IReadOnlyDictionary<GeometryCellKey, GeometryCellRecord> RubbleCells => _rubbleCells;

    // Persistence checkpoint change tracking (off until EnableCheckpointTracking; client maps never
    // turn it on). Every cube position change and every rubble change (set, removed, a piece moved)
    // is recorded, so an incremental checkpoint visits only those. HashSet adds reuse capacity, so
    // a moving world adds no per-tick allocation once the sets have grown.
    private HashSet<Cube>? _checkpointMovedCubes;
    private HashSet<GeometryCellKey>? _checkpointDirtyRubbleCells;

    public Map(
        PhysicsIntegrator? physics = null,
        bool generateStartupTerrain = false,
        int width = 25,
        int height = 8,
        int depth = 25,
        bool replicatedCubeBodies = false,
        bool headless = false)
    {
        _physics = physics;
        IsHeadless = headless;
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
}
