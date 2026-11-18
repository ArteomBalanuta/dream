using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Server.Persistence;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-BUILD-1 and BH-BUILD-2: TryResolvePersistedPlacedCube matched a persisted row to a cube by
// cell alone, so a brand-new placement inherited the ObjectId, owner, actor id and damaged health
// of whatever cube had last been persisted at that cell. Two sources of such a stale row:
//   BH-BUILD-1 a dynamic cube pushed out of its cell (its row's CellPosition only refreshes at the
//              next checkpoint, ~5 s later);
//   BH-BUILD-2 a restore-skipped row, deliberately kept in _persistedCubes to reserve its id but
//              never bound to an actor id.
// Identity is now bound to the row's own ObjectId -> actor id binding, and the cell is only used to
// find the candidate row. The controls below pin the repair the lookup exists for, which must keep
// working: it runs before the build-protection gate, so refusing it would hand a player's wall to
// a stranger.
public class BuildCubeIdentityByCellTests
{
    private const string Alice = "alice";
    private const string Bob = "bob";
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Vector3 Cell = new(3, 1, 3);

    // BH-BUILD-1
    [Fact]
    public void APlacementInTheCellAMovedCubeWasPersistedAt_GetsAFreshIdentity_AndLeavesTheMovedCubeIntact()
    {
        Rig rig = new();
        Map map = rig.Host.Map;

        // Player A builds a cube and it takes damage.
        Cube moved = map.TryAddCube(Cell)!;
        WorldObjectRecord movedRecord = rig.WorldObjects.RegisterCube(moved, map, ownerPlayerName: Alice);
        movedRecord.Health.SetAuthoritativeState(20, 100);
        rig.Bridge.PersistPlacedCube(movedRecord, GeometryMaterialId.Grass);
        Assert.True(rig.Identities.TryGetObjectId(movedRecord.Actor.Id, out ObjectId movedId));

        // Physics pushes it one cell over. The row still says (3,1,3) until the next checkpoint.
        Assert.Null(map.Cubes[4, 1, 3]);
        map.UpdateCubePosition(moved, new Vector3(4.5f, 1.5f, 3.5f));
        Assert.True(map.TryGetCellForCube(moved, out GeometryCellKey movedCell));
        Assert.Equal(new GeometryCellKey(4, 1, 3), movedCell);

        // Player B builds in the cell that just came free.
        Cube placed = map.TryAddCube(Cell)!;
        WorldObjectRecord placedRecord = rig.WorldObjects.RegisterCube(placed, map, ownerPlayerName: Bob);
        int freshActorId = placedRecord.Actor.Id;
        rig.Bridge.PersistPlacedCube(placedRecord, GeometryMaterialId.Grass);

        // Before the fix this record came back owned by alice, at 20 health, carrying alice's actor id.
        WorldObjectRecord afterPlace = rig.WorldObjects.TryGet(placed)!;
        Assert.Equal(Bob, afterPlace.OwnerPlayerName);
        Assert.Equal(100, afterPlace.Health.CurrentHealth);
        Assert.Equal(freshActorId, afterPlace.Actor.Id);
        Assert.True(rig.Identities.TryGetObjectId(afterPlace.Actor.Id, out ObjectId placedId));
        Assert.NotEqual(movedId, placedId);

        // A's cube keeps its record, its owner, its damage and its ObjectId binding, so it is still
        // checkpointed and survives a restart.
        Assert.Same(movedRecord, rig.WorldObjects.TryGet(moved));
        Assert.Equal(Alice, movedRecord.OwnerPlayerName);
        Assert.Equal(20, movedRecord.Health.CurrentHealth);
        Assert.True(rig.Identities.TryGetActorId(movedId, out int stillBound));
        Assert.Equal(movedRecord.Actor.Id, stillBound);

        // Two cubes on one replicated id is what misrouted every CubeMoved delta.
        Assert.True(map.TryGetReplicatedId(moved, out int movedReplicated));
        Assert.True(map.TryGetReplicatedId(placed, out int placedReplicated));
        Assert.NotEqual(movedReplicated, placedReplicated);

        // Both rows are in the database, each at its own cell.
        List<PersistedPlacedCube> rows = [.. rig.World.LoadPlacedCubes()];
        Assert.Equal(2, rows.Count);
        PersistedPlacedCube movedRow = Assert.Single(rows, row => row.ObjectId == movedId);
        Assert.Equal(Alice, movedRow.OwnerPlayerName);
        Assert.Equal(20, movedRow.CurrentHealth);
        PersistedPlacedCube placedRow = Assert.Single(rows, row => row.ObjectId == placedId);
        Assert.Equal(Bob, placedRow.OwnerPlayerName);
        Assert.Equal(100, placedRow.CurrentHealth);
        Assert.Equal(Cell, placedRow.CellPosition);
    }

    // BH-BUILD-2
    [Fact]
    public void APlacementInTheCellOfARestoreSkippedOrphanRow_GetsAFreshIdentity_AndLeavesTheOrphanRowAlone()
    {
        Rig rig = new();

        // Two rows name one cell: RestoreWorld (ObjectId order) adopts the first and skips the
        // second, whose row stays in _persistedCubes with no actor binding at all.
        rig.World.SavePlacedCube(Row(new ObjectId(10), Alice, currentHealth: 100));
        rig.World.SavePlacedCube(Row(new ObjectId(11), Alice, currentHealth: 20));
        rig.Bridge.RestoreWorld();

        Map map = rig.Host.Map;
        Cube restored = map.TryGetCubeAtCell(3, 1, 3)!;
        WorldObjectRecord restoredRecord = rig.WorldObjects.TryGet(restored)!;
        Assert.True(rig.Identities.TryGetActorId(new ObjectId(10), out _));
        Assert.False(rig.Identities.TryGetActorId(new ObjectId(11), out _));

        // The occupant is destroyed, so the cell comes free while the orphan row still names it.
        rig.Bridge.DeletePersistedPlacedCube(restoredRecord.Actor.Id);
        Assert.True(rig.WorldObjects.Unregister(restored, preserveDetachedRecord: false));
        Assert.True(map.RemoveCube(restored));

        Cube placed = map.TryAddCube(Cell)!;
        WorldObjectRecord placedRecord = rig.WorldObjects.RegisterCube(placed, map, ownerPlayerName: Bob);
        int freshActorId = placedRecord.Actor.Id;
        rig.Bridge.PersistPlacedCube(placedRecord, GeometryMaterialId.Grass);

        // Before the fix this inherited alice's ownership and her 20 health under ObjectId 11.
        WorldObjectRecord afterPlace = rig.WorldObjects.TryGet(placed)!;
        Assert.Equal(Bob, afterPlace.OwnerPlayerName);
        Assert.Equal(100, afterPlace.Health.CurrentHealth);
        Assert.Equal(freshActorId, afterPlace.Actor.Id);
        Assert.True(rig.Identities.TryGetObjectId(afterPlace.Actor.Id, out ObjectId placedId));
        Assert.NotEqual(new ObjectId(10), placedId);
        Assert.NotEqual(new ObjectId(11), placedId);

        // The orphan row is untouched: it still describes the cube it was written for.
        List<PersistedPlacedCube> rows = [.. rig.World.LoadPlacedCubes()];
        Assert.Equal(2, rows.Count);
        PersistedPlacedCube orphan = Assert.Single(rows, row => row.ObjectId == new ObjectId(11));
        Assert.Equal(Alice, orphan.OwnerPlayerName);
        Assert.Equal(20, orphan.CurrentHealth);
        Assert.Equal(Bob, Assert.Single(rows, row => row.ObjectId == placedId).OwnerPlayerName);
    }

    // Control: the repair the lookup exists for. A record whose registry link was lost carries a
    // null owner, and only this repair restores the persisted one before the protection gate runs.
    [Fact]
    public void ARecordThatLostItsRegistryLink_IsStillRepairedToItsPersistedOwnerHealthAndObjectId()
    {
        Rig rig = new();
        Map map = rig.Host.Map;
        Cube cube = map.TryAddCube(Cell)!;
        WorldObjectRecord original = rig.WorldObjects.RegisterCube(cube, map, ownerPlayerName: Alice);
        original.Health.SetAuthoritativeState(40, 100);
        rig.Bridge.PersistPlacedCube(original, GeometryMaterialId.Grass);
        Assert.True(rig.Identities.TryGetObjectId(original.Actor.Id, out ObjectId objectId));

        // The resolver's fresh-registration branch: a new actor id and no owner.
        Assert.True(rig.WorldObjects.Unregister(cube, preserveDetachedRecord: false));
        WorldObjectRecord fresh = rig.WorldObjects.TryResolveCubeRecord(map, cube)!;
        Assert.Null(fresh.OwnerPlayerName);
        Assert.NotEqual(original.Actor.Id, fresh.Actor.Id);

        WorldObjectRecord repaired = rig.Bridge.RepairCubeRecordFromPersistence(fresh);

        Assert.Equal(Alice, repaired.OwnerPlayerName);
        Assert.Equal(40, repaired.Health.CurrentHealth);
        Assert.Equal(100, repaired.Health.MaxHealth);
        Assert.Equal(original.Actor.Id, repaired.Actor.Id);
        Assert.True(rig.Identities.TryGetObjectId(repaired.Actor.Id, out ObjectId repairedId));
        Assert.Equal(objectId, repairedId);
    }

    // Control: the ordinary hit path. A record that still matches its row is returned untouched,
    // keeping the damage it took since the last checkpoint.
    [Fact]
    public void ARecordThatStillMatchesItsRow_IsReturnedUnchangedWithItsLiveHealth()
    {
        Rig rig = new();
        Map map = rig.Host.Map;
        Cube cube = map.TryAddCube(Cell)!;
        WorldObjectRecord record = rig.WorldObjects.RegisterCube(cube, map, ownerPlayerName: Alice);
        rig.Bridge.PersistPlacedCube(record, GeometryMaterialId.Grass);
        record.Health.SetAuthoritativeState(55, 100);
        rig.Bridge.UpdatePersistedCubeHealth(record);

        WorldObjectRecord repaired = rig.Bridge.RepairCubeRecordFromPersistence(record);

        Assert.Same(record, repaired);
        Assert.Equal(Alice, repaired.OwnerPlayerName);
        Assert.Equal(55, repaired.Health.CurrentHealth);
    }

    // Control: destroy and rebuild in the same cell. The rebuilt cube is a new object with a new
    // owner, a new ObjectId and full health.
    [Fact]
    public void ACubeRebuiltInADestroyedCubesCell_GetsAFreshIdentityAndFullHealth()
    {
        Rig rig = new();
        Map map = rig.Host.Map;
        Cube first = map.TryAddCube(Cell)!;
        WorldObjectRecord firstRecord = rig.WorldObjects.RegisterCube(first, map, ownerPlayerName: Alice);
        firstRecord.Health.SetAuthoritativeState(10, 100);
        rig.Bridge.PersistPlacedCube(firstRecord, GeometryMaterialId.Grass);
        Assert.True(rig.Identities.TryGetObjectId(firstRecord.Actor.Id, out ObjectId firstId));

        rig.Bridge.DeletePersistedPlacedCube(firstRecord.Actor.Id);
        Assert.True(rig.WorldObjects.Unregister(first, preserveDetachedRecord: false));
        Assert.True(map.RemoveCube(first));

        Cube second = map.TryAddCube(Cell)!;
        WorldObjectRecord secondRecord = rig.WorldObjects.RegisterCube(second, map, ownerPlayerName: Bob);
        rig.Bridge.PersistPlacedCube(secondRecord, GeometryMaterialId.Grass);

        WorldObjectRecord after = rig.WorldObjects.TryGet(second)!;
        Assert.Equal(Bob, after.OwnerPlayerName);
        Assert.Equal(100, after.Health.CurrentHealth);
        Assert.True(rig.Identities.TryGetObjectId(after.Actor.Id, out ObjectId secondId));
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(Bob, Assert.Single(rig.World.LoadPlacedCubes()).OwnerPlayerName);
    }

    private static PersistedPlacedCube Row(ObjectId objectId, string owner, int currentHealth)
    {
        return new PersistedPlacedCube(
            objectId,
            owner,
            Cell,
            GeometryShapeKind.SolidCube,
            GeometryMaterialId.Grass,
            currentHealth,
            100,
            Now,
            Now);
    }

    private sealed class Rig
    {
        private readonly IncrementalCheckpointTests.TempDatabase _database = new();

        public Rig()
        {
            new SqlitePlayerProfileRepository(_database.Schema).GetOrCreate(Alice);
            new SqlitePlayerProfileRepository(_database.Schema).GetOrCreate(Bob);
            World = new SqliteWorldPersistenceRepository(_database.Schema);
            Host = new FakeHost(new Map(generateStartupTerrain: true, width: 12, depth: 12, headless: true));
            ObjectIds = new ObjectIdAllocator();
            Identities = new ObjectIdentityMap();
            Bridge = new RuntimePersistenceBridge(
                Host,
                playerProfiles: null,
                World,
                checkpointWriter: null,
                PersistenceWriteQueue.Inline(),
                WorldObjects,
                Enemies,
                new SceneEventHub(),
                ObjectIds,
                Identities);
        }

        public SqliteWorldPersistenceRepository World { get; }

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
