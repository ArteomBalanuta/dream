using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// Wave H fix loop 1: how the inventory's queued writes behave on real SQLite when one of them fails
// (a trigger aborts it, the queue counts it in FailedWrites and drops it) and when the process stops
// between two of them. The service keeps its in-memory copy, so the rows can fall behind it.
public sealed class InventoryWriteFailureTests
{
    private const string Player = "Alice";

    private static MigrationTestDatabase Migrated()
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqliteConnection.ClearAllPools();
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('Alice', 'Alice');");
        return db;
    }

    // One server run on a background queue, so a failed closure is rolled back as the queue does it.
    private sealed class Run : IDisposable
    {
        private readonly ServerPersistenceBundle _bundle;
        private readonly PersistenceWriteQueue _writes = PersistenceWriteQueue.Background();

        public Run(MigrationTestDatabase db)
        {
            _bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
            Inventory = new PlayerInventoryService(_bundle.World, _writes, new ObjectIdAllocator(_bundle.World.LoadNextObjectId()));
        }

        public PlayerInventoryService Inventory { get; }

        public IWorldPersistenceRepository World => _bundle.World;

        public long FailedWrites => _writes.FailedWrites;

        public void Flush() => Assert.True(_writes.Flush(TimeSpan.FromSeconds(30)));

        public Dictionary<int, long> Rows() =>
            World.LoadPlayerInventory(Player).Slots.ToDictionary(slot => slot.SlotIndex, slot => slot.ItemInstanceId);

        public void Dispose()
        {
            _writes.Dispose();
            _bundle.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    private static void Give(IWorldPersistenceRepository world, int slot, long id, string kind)
    {
        world.SaveItemInstance(EquipmentTestWorld.MakeItem(id, Player, kind));
        world.SaveInventorySlot(new PersistedInventorySlot(Player, slot, id, DateTime.UtcNow));
    }

    private static void SeedTwoItems(MigrationTestDatabase db)
    {
        using Run setup = new(db);
        Give(setup.World, 0, 10, "tool.torch");
        Give(setup.World, 1, 11, "tool.builder-hammer");
        setup.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
    }

    [Fact]
    public void AFailedSwap_ThenAMoveOfTheDivergedSlot_DoesNotDeleteTheOtherItemsRow()
    {
        using MigrationTestDatabase db = Migrated();
        SeedTwoItems(db);
        using Run run = new(db);
        Assert.True(run.Inventory.Ensure(Player));

        // The swap's closure aborts: memory says 0=B, 1=A, the rows still say 0=A, 1=B.
        db.ExecuteScript("CREATE TRIGGER fail_swap BEFORE UPDATE ON player_inventory_slots WHEN NEW.slot_index = 0 BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryMove(Player, 0, 1));
        run.Flush();
        Assert.Equal(1, run.FailedWrites);
        Assert.Equal(10, run.Rows()[0]);
        Assert.Equal(11, run.Rows()[1]);
        db.ExecuteScript("DROP TRIGGER fail_swap;");

        // Moving A from slot 1 to 5 deletes slot 1 only if it still holds A. It holds B, so B keeps its row.
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryMove(Player, 1, 5));
        run.Flush();
        Assert.Equal(1, run.FailedWrites);
        Dictionary<int, long> rows = run.Rows();
        Assert.Equal(11, rows[1]);
        Assert.Equal(10, rows[5]);
        Assert.Contains(11L, rows.Values);
    }

    [Fact]
    public void TheExpectedItemDelete_OnRealSqlite_RemovesTheRowOnlyWhileItHoldsThatItem()
    {
        using MigrationTestDatabase db = Migrated();
        SeedTwoItems(db);
        using Run run = new(db);

        run.World.DeleteInventorySlot(Player, 1, 10);
        Assert.Equal(11, run.Rows()[1]);

        run.World.DeleteInventorySlot(Player, 1, 11);
        Assert.False(run.Rows().ContainsKey(1));
        Assert.Equal(10, run.Rows()[0]);
    }

    [Fact]
    public void AStarterKitThatIsQueued_PutsTheMainHandBeforeTheGrantRecord()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(
            "CREATE TABLE write_log (id INTEGER PRIMARY KEY AUTOINCREMENT, what TEXT NOT NULL);" +
            "CREATE TRIGGER log_equip AFTER INSERT ON player_inventory_slots WHEN NEW.slot_index = 100 BEGIN INSERT INTO write_log (what) VALUES ('equip'); END;" +
            "CREATE TRIGGER log_grant AFTER INSERT ON player_starter_grants BEGIN INSERT INTO write_log (what) VALUES ('grant'); END;");
        using (Run run = new(db))
        {
            Assert.True(run.Inventory.Ensure(Player));
            run.Flush();
            Assert.Equal(0, run.FailedWrites);
        }

        Assert.Equal(
            "equip,grant",
            string.Join(",", Enumerable.Range(1, 2).Select(i => db.Scalar($"SELECT what FROM write_log WHERE id = {i};"))));
    }

    [Fact]
    public void ACrashBeforeTheGrantRow_LeavesNoRecord_AndTheNextJoinRepairsTheEmptyHand()
    {
        using MigrationTestDatabase db = Migrated();
        // The stop after the kit and its slots but before the grant row: no record, no main hand.
        using (Run first = new(db))
        {
            Assert.True(first.Inventory.Ensure(Player));
            first.Flush();
        }

        db.ExecuteScript("DELETE FROM player_starter_grants; DELETE FROM player_inventory_slots WHERE slot_index = 100;");

        using Run restarted = new(db);
        Assert.True(restarted.Inventory.Ensure(Player));
        restarted.Flush();

        Assert.Equal(0, restarted.FailedWrites);
        Assert.NotNull(restarted.Inventory.BuildSnapshot(Player).EquippedSlot!.Value.Item);
        Assert.True(restarted.Rows().ContainsKey(100));
        Assert.Equal((long)ToolCatalog.CurrentStarterKitVersion, db.Scalar("SELECT kit_version FROM player_starter_grants;"));
        Assert.Equal(
            (long)ToolCatalog.StarterKit.Count,
            db.Scalar("SELECT COUNT(*) FROM player_inventory_slots WHERE slot_index < 100;"));
    }

    [Fact]
    public void AGrantedItem_LandsWithItsSlotRow_OrNotAtAll()
    {
        using MigrationTestDatabase db = Migrated();
        SeedTwoItems(db);
        using Run run = new(db);
        Assert.True(run.Inventory.Ensure(Player));
        ItemRollSeed seed = new(1, 5, 7, 0, 0);

        db.ExecuteScript("CREATE TRIGGER fail_slot2 BEFORE INSERT ON player_inventory_slots WHEN NEW.slot_index = 2 BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryGrantRolled(Player, "gear.iron-blade", ItemRarity.Rare, in seed));
        run.Flush();

        Assert.Equal(1, run.FailedWrites);
        Assert.Equal(2L, db.Scalar("SELECT COUNT(*) FROM item_instances;"));
        db.ExecuteScript("DROP TRIGGER fail_slot2;");

        ItemRollSeed second = new(1, 6, 7, 0, 1);
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryGrantRolled(Player, "gear.leather-cap", ItemRarity.Rare, in second));
        run.Flush();

        Assert.Equal(1, run.FailedWrites);
        Assert.Equal(3L, db.Scalar("SELECT COUNT(*) FROM item_instances;"));
        long newest = (long)db.Scalar("SELECT MAX(item_instance_id) FROM item_instances;")!;
        Assert.Equal(1L, db.Scalar($"SELECT COUNT(*) FROM player_inventory_slots WHERE item_instance_id = {newest};"));
    }
}
