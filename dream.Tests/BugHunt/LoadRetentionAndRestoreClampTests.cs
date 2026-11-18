using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Support;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.BugHunt;

// BUGHUNTER-LOAD: the two findings of the load/soak sweep, both of them things a short run cannot see.
// BH-LOAD-1 PlayerInventoryService.Evict dropped every per-character map except the two revision
//           dictionaries, so a server kept one entry pair per character that ever departed.
// BH-LOAD-2 RuntimePersistenceBridge restored an enemy at the stored position unclamped while
//           EnemyFactory.Restore clamps the level, the health pool and every stat of the same row.
public sealed class LoadRetentionAndRestoreClampTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    // BH-LOAD-1. Fifty join/evict cycles, each a distinct character, each queueing a write (the starter
    // kit grant) so Evict takes the write-tracker branch. Deterministic: an inline write queue confirms
    // every write before Evict returns, so nothing depends on a background worker or on wall-clock time.
    // Before the fix TrackedRevisionCount was 2 per cycle and never fell.
    [Fact]
    public void RepeatedJoinAndEvictCycles_LeaveNoRevisionEntryBehind()
    {
        EquipmentTestWorld world = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = new(world, writes, new ObjectIdAllocator());

        for (int cycle = 0; cycle < 50; cycle++)
        {
            string player = $"visitor-{cycle}";
            Assert.True(inventory.Ensure(player));
            // The starter kit grant queued a write, so the tracker branch of Evict is the one taken.
            Assert.True(inventory.InventoryRevisionOf(player) > 0);

            inventory.Evict(player);

            Assert.Equal(0, inventory.LoadedInventoryCount);
            Assert.Equal(0, inventory.UnconfirmedEvictionCount);
            Assert.Equal(0, inventory.InventoryRevisionOf(player));
            // The point of the finding: the maps are sized by the players online, not by lifetime joins.
            Assert.Equal(0, inventory.TrackedRevisionCount);
        }
    }

    // BH-LOAD-1 control: a loaded inventory keeps its revision, and a second player's entry is not
    // disturbed by the first's eviction. A reloaded inventory restarting at zero is harmless: the
    // quest-log sync is per session and its two revisions start at -1 (WorldSessionRuntime.Quests
    // QuestLogSync), so the first pass after a rejoin always differs and resends.
    [Fact]
    public void ALoadedInventoryKeepsItsRevision_AndAnEvictionLeavesTheOtherPlayersAlone()
    {
        EquipmentTestWorld world = new();
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Inline();
        PlayerInventoryService inventory = new(world, writes, new ObjectIdAllocator());

        Assert.True(inventory.Ensure("alice"));
        Assert.True(inventory.Ensure("bob"));
        long alice = inventory.InventoryRevisionOf("alice");
        long bob = inventory.InventoryRevisionOf("bob");
        Assert.True(alice > 0);
        Assert.True(bob > 0);
        // Two entries, both in _inventoryRevisions: neither player has changed a quest, so
        // _questRevisions has none yet. Either way the count tracks the loaded inventories.
        Assert.Equal(2, inventory.TrackedRevisionCount);

        inventory.Evict("alice");

        Assert.Equal(0, inventory.InventoryRevisionOf("alice"));
        Assert.Equal(bob, inventory.InventoryRevisionOf("bob"));
        Assert.Equal(1, inventory.TrackedRevisionCount);
    }

    // BH-LOAD-2. A row whose position is far outside this map (a world exported onto a smaller floor, a
    // physics excursion past the edge) restores inside the playable area, against the same bound
    // EnemyRegistry.ClampToMapBounds and the floor-resize path use. Before the fix the actor restored at
    // (40, 1, 40) on a 12x12 map and every checkpoint wrote the same position back.
    [Fact]
    public void AnEnemyRowOutsideTheMap_RestoresInsideIt()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        world.SaveEnemyInstance(Row(new Vector3(40f, 1f, 40f)));

        Rig rig = new(world);
        rig.Bridge.RestoreWorld();

        EnemyActor restored = Assert.Single(rig.Enemies.ActiveActors);
        Assert.InRange(restored.Position.X, 0.5f, rig.Host.Map.Width - 0.5f);
        Assert.InRange(restored.Position.Z, 0.5f, rig.Host.Map.Depth - 0.5f);
        Assert.Equal(new Vector3(rig.Host.Map.Width - 0.5f, 1f, rig.Host.Map.Depth - 0.5f), restored.Position);
    }

    // BH-LOAD-2 control: a row inside the map restores at exactly its stored position, bit for bit.
    [Fact]
    public void AnEnemyRowInsideTheMap_RestoresExactlyWhereItWas()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        Vector3 stored = new(7.25f, 1f, 3.5f);
        world.SaveEnemyInstance(Row(stored));

        Rig rig = new(world);
        rig.Bridge.RestoreWorld();

        Assert.Equal(stored, Assert.Single(rig.Enemies.ActiveActors).Position);
    }

    // BH-LOAD-2, the non-finite half. Math.Clamp(float) returns the value when both comparisons are
    // false, so a clamp alone passes infinity and NaN straight through - the same hole the
    // movement_speed clamp in EnemyFactory.Restore was written for. 9e999 is how a non-finite REAL
    // reaches a row in practice (a hand edit or an import), since the binder cannot write one.
    [Fact]
    public void AnEnemyRowWithANonFinitePosition_RestoresAtAFinitePositionInsideTheMap()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        world.SaveEnemyInstance(Row(new Vector3(6f, 1f, 6f)));
        using (SqliteConnection connection = database.Schema.CreateOpenConnection())
        {
            using SqliteCommand poison = connection.CreateCommand();
            poison.CommandText = "UPDATE enemy_instances SET position_x = 9e999, position_z = -9e999;";
            Assert.Equal(1, poison.ExecuteNonQuery());
        }

        Assert.False(float.IsFinite(Assert.Single(world.LoadActiveEnemies()).Position.X));

        Rig rig = new(world);
        rig.Bridge.RestoreWorld();

        EnemyActor restored = Assert.Single(rig.Enemies.ActiveActors);
        Assert.True(float.IsFinite(restored.Position.X), $"{restored.Position}");
        Assert.True(float.IsFinite(restored.Position.Y), $"{restored.Position}");
        Assert.True(float.IsFinite(restored.Position.Z), $"{restored.Position}");
        Assert.InRange(restored.Position.X, 0.5f, rig.Host.Map.Width - 0.5f);
        Assert.InRange(restored.Position.Z, 0.5f, rig.Host.Map.Depth - 0.5f);
    }

    // BH-LOAD-2, the sibling column found by the field audit: facing_x/y/z carry no CHECK either, and
    // EnemyActor.FaceDirection only rejects a near-zero direction - a non-finite one normalizes to a
    // non-finite Forward. Such a row keeps the actor's default forward.
    [Fact]
    public void AnEnemyRowWithANonFiniteFacing_KeepsAFiniteForward()
    {
        PersistedEnemyInstance row = Row(new Vector3(6f, 1f, 6f)) with
        {
            Facing = new Vector3(float.NaN, 0f, float.NaN),
        };

        EnemyActor restored = EnemyFactory.Restore(
            row,
            ContentCatalog.Shipped.Wolf,
            actorId: 2100,
            new SceneEventHub(),
            CombatFormulaService.Default);

        Assert.True(float.IsFinite(restored.Forward.X), $"{restored.Forward}");
        Assert.True(float.IsFinite(restored.Forward.Z), $"{restored.Forward}");
        Assert.True(restored.Forward.LengthSquared > 0f);
    }

    // BH-LOAD-2 control: a finite facing is still applied exactly as stored.
    [Fact]
    public void AnEnemyRowWithAFiniteFacing_StillFacesIt()
    {
        PersistedEnemyInstance row = Row(new Vector3(6f, 1f, 6f)) with { Facing = Vector3.UnitX };

        EnemyActor restored = EnemyFactory.Restore(
            row,
            ContentCatalog.Shipped.Wolf,
            actorId: 2101,
            new SceneEventHub(),
            CombatFormulaService.Default);

        Assert.Equal(Vector3.UnitX, restored.Forward);
    }

    private static PersistedEnemyInstance Row(Vector3 position) => new(
        new ObjectId(2000),
        ContentCatalog.Shipped.Wolf.Key,
        ContentCatalog.Shipped.Wolf.DisplayName,
        position,
        Vector3.UnitZ,
        new PersistedCreatureStats(40, 40, 8, 10, 10, 0, 2.8f, 1f, 1),
        Now,
        Now);

    private sealed class Rig
    {
        public Rig(IWorldPersistenceRepository world)
        {
            Host = new FakeHost(new Map(generateStartupTerrain: true, width: 12, depth: 12, headless: true));
            Bridge = new RuntimePersistenceBridge(
                Host,
                playerProfiles: null,
                world,
                checkpointWriter: null,
                PersistenceWriteQueue.Inline(),
                WorldObjects,
                Enemies,
                new SceneEventHub(),
                new ObjectIdAllocator(),
                new ObjectIdentityMap());
        }

        public FakeHost Host { get; }

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

        public PlayerInteractionMode GetSessionInteractionMode(PlayerSessionId sessionId) => PlayerInteractionMode.Combat;

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
