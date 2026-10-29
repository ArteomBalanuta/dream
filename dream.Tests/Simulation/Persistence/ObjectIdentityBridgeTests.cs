using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.Persistence;

// Wave C (B): RuntimePersistenceBridge's ObjectId identity for cubes, rubble and enemies, against a
// real repository and a fake runtime host. Wire, checksum and RNG identity stay the actor id.
public sealed class ObjectIdentityBridgeTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Restore_HandsOutActorIdsInObjectIdOrder_AndBindsEachCube()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate("builder");
        SqliteWorldPersistenceRepository world = new(database.Schema);
        // Saved out of order on purpose: the actor ids follow the ObjectIds, not the save order.
        world.SavePlacedCube(Cube(30, new Vector3(5, 1, 5)));
        world.SavePlacedCube(Cube(10, new Vector3(1, 1, 1)));
        world.SavePlacedCube(Cube(20, new Vector3(3, 1, 3)));
        Rig rig = new(world);

        rig.Bridge.RestoreWorld();

        foreach ((long objectId, int expectedActorId) in new[] { (10L, 1000), (20L, 1001), (30L, 1002) })
        {
            Assert.True(rig.Identities.TryGetActorId(new ObjectId(objectId), out int actorId));
            Assert.Equal(expectedActorId, actorId);
        }

        // The allocator resumes past every loaded id.
        Assert.Equal(new ObjectId(31), rig.ObjectIds.Take());
    }

    [Fact]
    public void ACubeWithNoOwner_IsStillCheckpointed_WhenItHasAnObjectId()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate("builder");
        Rig rig = new(new SqliteWorldPersistenceRepository(database.Schema));
        Cube cube = rig.Host.Map.TryAddCube(new Vector3(3, 1, 3))!;
        WorldObjectRecord record = rig.WorldObjects.RegisterCube(cube, rig.Host.Map, ownerPlayerName: null);
        ObjectId id = rig.ObjectIds.Take();
        rig.Identities.Bind(id, record.Actor.Id);

        PersistenceCheckpoint checkpoint = rig.Bridge.CapturePersistenceCheckpoint(full: true)!;

        PersistedPlacedCube captured = Assert.Single(checkpoint.PlacedCubes);
        Assert.Equal(id, captured.ObjectId);
        Assert.Null(captured.OwnerPlayerName);

        // Without a binding (a terrain cube) the cube is not captured.
        Cube terrain = rig.Host.Map.TryAddCube(new Vector3(5, 1, 5))!;
        rig.WorldObjects.RegisterCube(terrain, rig.Host.Map, ownerPlayerName: "builder");
        Assert.Single(rig.Bridge.CapturePersistenceCheckpoint(full: true)!.PlacedCubes);
    }

    // The combat resolver deletes the destroyed cube's row, then persists its rubble, in one tick:
    // the delete removes the binding, so the origin has to come from the retired-cube slot.
    [Fact]
    public void DeleteThenFracture_GivesEachPieceItsOwnId_AndKeepsTheCubesObjectIdAsTheOrigin()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate("builder");
        Rig rig = new(new SqliteWorldPersistenceRepository(database.Schema));
        Map map = rig.Host.Map;
        Cube cube = map.TryAddCube(new Vector3(4, 2, 4))!;
        WorldObjectRecord record = rig.WorldObjects.RegisterCube(cube, map, actorId: 42, ownerPlayerName: "builder");
        rig.Bridge.PersistPlacedCube(record, GeometryMaterialId.Grass);
        Assert.True(rig.Identities.TryGetObjectId(42, out ObjectId origin));
        RestoreRubble(map, new GeometryCellKey(4, 2, 4), pieces: 3);

        rig.Bridge.DeletePersistedPlacedCube(42);
        Assert.False(rig.Identities.TryGetObjectId(42, out _));
        rig.Bridge.PersistFracturePieces(record, new GeometryCellKey(4, 2, 4));

        PersistenceCheckpoint checkpoint = rig.Bridge.CapturePersistenceCheckpoint(full: true)!;
        Assert.Equal(3, checkpoint.FracturePieces.Count);
        Assert.All(checkpoint.FracturePieces, piece => Assert.Equal(origin, piece.OriginObjectId));
        ObjectId[] pieceIds = [.. checkpoint.FracturePieces.OrderBy(piece => piece.PieceIndex).Select(piece => piece.ObjectId)];
        Assert.Equal(3, pieceIds.Distinct().Count());
        Assert.All(pieceIds, id => Assert.True(!id.IsNone && id != origin));
        Assert.Empty(checkpoint.PlacedCubes);
    }

    [Fact]
    public void TheRetiredCubeSlot_ServesOnlyTheNextFractureOfThatCube()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate("builder");
        Rig rig = new(new SqliteWorldPersistenceRepository(database.Schema));
        Map map = rig.Host.Map;
        Cube cube = map.TryAddCube(new Vector3(4, 2, 4))!;
        WorldObjectRecord record = rig.WorldObjects.RegisterCube(cube, map, actorId: 42, ownerPlayerName: "builder");
        rig.Bridge.PersistPlacedCube(record, GeometryMaterialId.Grass);
        Assert.True(rig.Identities.TryGetObjectId(42, out ObjectId retired));
        RestoreRubble(map, new GeometryCellKey(4, 2, 4), pieces: 1);
        RestoreRubble(map, new GeometryCellKey(8, 2, 8), pieces: 1);

        rig.Bridge.DeletePersistedPlacedCube(42);
        rig.Bridge.PersistFracturePieces(record, new GeometryCellKey(4, 2, 4));
        // A second fracture of the same record has no retired cube left to resolve: it is a new origin.
        rig.Bridge.PersistFracturePieces(record, new GeometryCellKey(8, 2, 8));

        PersistenceCheckpoint checkpoint = rig.Bridge.CapturePersistenceCheckpoint(full: true)!;
        PersistedCubeFracturePiece first = Assert.Single(checkpoint.FracturePieces, piece => piece.ParentCell == new Vector3(4, 2, 4));
        PersistedCubeFracturePiece second = Assert.Single(checkpoint.FracturePieces, piece => piece.ParentCell == new Vector3(8, 2, 8));
        Assert.Equal(retired, first.OriginObjectId);
        Assert.NotEqual(retired, second.OriginObjectId);
        Assert.NotEqual(first.ObjectId, second.ObjectId);
    }

    [Fact]
    public void ARespawn_TakesANewObjectId_AndNothingIsAllocatedWithoutPersistence()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate("builder");
        Rig rig = new(new SqliteWorldPersistenceRepository(database.Schema));
        EnemyActor first = Wolf(2000);
        rig.Enemies.Add(first);
        rig.Bridge.PersistEnemy(first);
        Assert.True(rig.Identities.TryGetObjectId(2000, out ObjectId firstId));

        rig.Bridge.DeletePersistedEnemy(2000);
        EnemyActor second = Wolf(2001);
        rig.Bridge.PersistEnemy(second);
        Assert.True(rig.Identities.TryGetObjectId(2001, out ObjectId secondId));
        Assert.True(secondId.Value > firstId.Value);
        Assert.False(rig.Identities.TryGetObjectId(2000, out _));
        // Deleting an enemy that was never persisted is a no-op.
        rig.Bridge.DeletePersistedEnemy(2999);

        Rig off = new(world: null);
        off.Bridge.PersistEnemy(Wolf(2000));
        Assert.Equal(0, off.Identities.Count);
        Assert.Equal(1, off.ObjectIds.Next);
    }

    [Fact]
    public void TombstonesAndPieces_MergeByObjectId()
    {
        ObjectId cube = new(7);
        ObjectId enemy = new(8);
        ObjectId origin = new(9);
        PersistedCubeFracturePiece Piece(long id, long originId, int index, float side) =>
            new(new ObjectId(id), new ObjectId(originId), index, "builder", Vector3.One, Vector3.One, side, GeometryMaterialId.Grass, Now, Now);

        PersistenceCheckpoint older = new([], [], [], [Cube(7, Vector3.One)], [Piece(20, 9, 0, 0.5f), Piece(21, 9, 1, 0.5f)])
        {
            IsIncremental = true
        };
        PersistenceCheckpoint newer = new([], [], [], [], [Piece(21, 9, 1, 0.25f), Piece(22, 10, 1, 0.5f)])
        {
            IsIncremental = true,
            RemovedCubeObjectIds = [cube],
            RemovedEnemyObjectIds = [enemy]
        };

        PersistenceCheckpoint merged = PersistenceCheckpoint.Merge(older, newer);

        Assert.Empty(merged.PlacedCubes);
        Assert.Equal([cube], merged.RemovedCubeObjectIds);
        Assert.Equal([enemy], merged.RemovedEnemyObjectIds);
        // Piece 21 is replaced by its newer row; the piece at the same index of another origin is its own piece.
        Assert.Equal(
            [(20L, 0.5f), (21L, 0.25f), (22L, 0.5f)],
            merged.FracturePieces.OrderBy(piece => piece.ObjectId).Select(piece => (piece.ObjectId.Value, piece.SideLength)));

        // A newer tombstone for the origin drops the older pieces of the group.
        PersistenceCheckpoint dropped = PersistenceCheckpoint.Merge(older, new PersistenceCheckpoint([], [], [], [], [])
        {
            IsIncremental = true,
            RemovedFractureOriginObjectIds = [origin]
        });
        Assert.Empty(dropped.FracturePieces);
        Assert.Equal([origin], dropped.RemovedFractureOriginObjectIds);
    }

    [Fact]
    public void AMapRebuild_KeepsTheBindings()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate("builder");
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        using WorldSessionRuntime runtime = new(
            12,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            persistenceWrites: writes);
        runtime.WriteCheckpointOnDispose = false;
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate("builder");
        Cube cube = runtime.Map.TryAddCube(new Vector3(7f, 1f, 7f))!;
        WorldObjectRecord record = WorldObjects(runtime).RegisterCube(cube, runtime.Map, ownerPlayerName: "builder");
        ObjectId id = runtime.ObjectIds.Take();
        runtime.ObjectIdentities.Bind(id, record.Actor.Id);

        runtime.RebuildMapForFloorSize(14);

        Assert.True(runtime.ObjectIdentities.TryGetActorId(id, out int actorId));
        Assert.Equal(record.Actor.Id, actorId);
        Assert.True(runtime.Map.TryGetReplicatedId(runtime.Map.TryGetCubeAtCell(7, 1, 7)!, out int rebuiltId));
        Assert.Equal(actorId, rebuiltId);
    }

    [Fact]
    public void ACaptureWithNothingChanged_AllocatesNothing()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate("builder");
        Rig rig = new(new SqliteWorldPersistenceRepository(database.Schema));
        // No periodic full sweep inside the measured iterations: a full capture allocates.
        rig.Bridge.FullCheckpointInterval = int.MaxValue;
        Map map = rig.Host.Map;
        for (int i = 0; i < 4; i++)
        {
            Cube cube = map.TryAddCube(new Vector3(1 + i, 1, 2))!;
            WorldObjectRecord record = rig.WorldObjects.RegisterCube(cube, map, ownerPlayerName: "builder");
            rig.Bridge.PersistPlacedCube(record, GeometryMaterialId.Grass);
        }

        EnemyActor wolf = Wolf(2000);
        rig.Enemies.Add(wolf);
        rig.Bridge.PersistEnemy(wolf);
        Assert.NotNull(rig.Bridge.CapturePersistenceCheckpoint(full: true));
        Assert.Null(rig.Bridge.CapturePersistenceCheckpoint(full: false));

        AllocationAssert.AtMost(0, () => rig.Bridge.CapturePersistenceCheckpoint(full: false));
    }

    private static PersistedPlacedCube Cube(long id, Vector3 cell) =>
        new(new ObjectId(id), "builder", cell, GeometryShapeKind.SolidCube, GeometryMaterialId.Grass, 100, 100, Now, Now);

    private static EnemyActor Wolf(int actorId)
    {
        Vector3 position = new(6f, 1f, 6f);
        return new EnemyActor(
            new EnemyCombatant(() => position, eventHub: null, actorId: actorId, maxHealth: 40, attackDamage: 1, attackIntervalSeconds: 2f),
            position,
            aggroRange: 0.1f,
            attackRange: 0.1f,
            moveSpeed: 0f,
            displayName: "Wolf");
    }

    private static void RestoreRubble(Map map, GeometryCellKey cell, int pieces)
    {
        GeometryFracturePiece[] list = new GeometryFracturePiece[pieces];
        for (int i = 0; i < pieces; i++)
        {
            list[i] = new GeometryFracturePiece(new Vector3(cell.X + 0.25f + (0.1f * i), cell.Y + 0.25f, cell.Z + 0.25f), 0.3f);
        }

        Assert.True(map.RestoreFracturePieces(cell, GeometryMaterialId.Grass, list, withPhysics: false));
    }

    private static WorldObjectRegistry WorldObjects(WorldSessionRuntime runtime) =>
        (WorldObjectRegistry)typeof(WorldSessionRuntime)
            .GetField("_worldObjects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(runtime)!;

    private sealed class Rig
    {
        public Rig(IWorldPersistenceRepository? world)
        {
            Host = new FakeHost(new Map(generateStartupTerrain: true, width: 12, depth: 12, headless: true));
            ObjectIds = new ObjectIdAllocator();
            Identities = new ObjectIdentityMap();
            Bridge = new RuntimePersistenceBridge(
                Host,
                playerProfiles: null,
                world,
                checkpointWriter: null,
                PersistenceWriteQueue.Inline(),
                WorldObjects,
                Enemies,
                new SceneEventHub(),
                ObjectIds,
                Identities);
        }

        public FakeHost Host { get; }

        public ObjectIdAllocator ObjectIds { get; }

        public ObjectIdentityMap Identities { get; }

        public WorldObjectRegistry WorldObjects { get; } = new();

        public EnemyRegistry Enemies { get; } = new();

        public RuntimePersistenceBridge Bridge { get; }
    }

    private sealed class FakeHost(Map map) : IRuntimePersistenceHost
    {
        private int _nextEnemyId = 3000;

        public Map Map { get; } = map;

        public PlayerSessionRegistry Sessions { get; } = new();

        public bool IsDisposed => false;

        public PlayerInteractionMode GetSessionInteractionMode(PlayerSessionId sessionId) => PlayerInteractionMode.Building;

        public bool TryGetPlayerCheckpointState(
            PlayerSessionId sessionId,
            out Vector3 position,
            out Vector3 facing,
            out int currentHealth,
            out int maxHealth)
        {
            position = default;
            facing = default;
            currentHealth = 0;
            maxHealth = 0;
            return false;
        }

        public bool TryAllocateEnemyActorId(out int actorId)
        {
            actorId = _nextEnemyId++;
            return true;
        }

        public void SpawnInitialWolf()
        {
        }

        public bool IsPlayerInventoryLoaded(string playerName) => false;

        public void EvictPlayerInventory(string playerName)
        {
        }
    }
}
