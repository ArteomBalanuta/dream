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
// Classic HUD iteration 2: the bag is a footprint board (a slot index is an anchor cell) and a worn item
// has no bag row, so a new player's rows are the hammer worn and four bag rows.
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

    // Two torches (1x2) side by side: anchor 0 and anchor 1, so a swap is in place.
    private static void SeedTwoItems(MigrationTestDatabase db)
    {
        using Run setup = new(db);
        Give(setup.World, 0, 10, "tool.torch");
        Give(setup.World, 1, 11, "tool.torch");
        setup.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
    }

    [Fact]
    public void AFailedSwap_ThenAMoveOfTheDivergedSlot_DoesNotDeleteTheOtherItemsRow()
    {
        using MigrationTestDatabase db = Migrated();
        SeedTwoItems(db);
        using Run run = new(db);
        Assert.True(run.Inventory.Ensure(Player));

        // The swap's closure aborts: memory says 0=B, 1=A, the rows still say 0=A, 1=B. (Two torches of the
        // same size swap in place: each takes the other's anchor.)
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

        // The hammer goes back to the bag, where it was before it was equipped.
        db.ExecuteScript("DELETE FROM player_starter_grants; UPDATE player_inventory_slots SET slot_index = 0 WHERE slot_index = 100;");

        using Run restarted = new(db);
        Assert.True(restarted.Inventory.Ensure(Player));
        restarted.Flush();

        Assert.Equal(0, restarted.FailedWrites);
        Assert.NotNull(restarted.Inventory.BuildSnapshot(Player).EquippedSlot!.Value.Item);
        Assert.True(restarted.Rows().ContainsKey(100));
        Assert.Equal((long)ToolCatalog.CurrentStarterKitVersion, db.Scalar("SELECT kit_version FROM player_starter_grants;"));
        // Every kit item is in the bag or worn, each in exactly one row.
        Assert.Equal(
            (long)ToolCatalog.StarterKit.Count - 1,
            db.Scalar("SELECT COUNT(*) FROM player_inventory_slots WHERE slot_index < 100;"));
        Assert.Equal((long)ToolCatalog.StarterKit.Count, db.Scalar("SELECT COUNT(DISTINCT item_instance_id) FROM player_inventory_slots;"));
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

    private static void AssertCompleteKit(MigrationTestDatabase db)
    {
        Assert.Equal((long)ToolCatalog.StarterKit.Count, db.Scalar("SELECT COUNT(*) FROM item_instances;"));
        foreach (StarterKitEntry entry in ToolCatalog.StarterKit)
        {
            Assert.Equal(1L, db.Scalar($"SELECT COUNT(*) FROM item_instances WHERE item_kind = '{entry.ItemKind}';"));
        }

        // The first kit item is in the main hand and the rest are in the bag: no item has two rows.
        Assert.Equal((long)ToolCatalog.StarterKit.Count - 1, db.Scalar("SELECT COUNT(*) FROM player_inventory_slots WHERE slot_index < 100;"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_inventory_slots WHERE slot_index = 100;"));
        Assert.Equal((long)ToolCatalog.StarterKit.Count, db.Scalar("SELECT COUNT(DISTINCT item_instance_id) FROM player_inventory_slots;"));
        Assert.Equal((long)ToolCatalog.CurrentStarterKitVersion, db.Scalar("SELECT kit_version FROM player_starter_grants;"));
    }

    [Theory]
    [InlineData("BEFORE INSERT ON player_inventory_slots WHEN NEW.slot_index = 2")]
    [InlineData("BEFORE INSERT ON player_inventory_slots WHEN NEW.slot_index = 100")]
    [InlineData("BEFORE INSERT ON player_starter_grants")]
    public void AFailedNewPlayerSeed_LeavesNoPartialKit_AndTheNextJoinSeedsTheCompleteKit(string triggerOn)
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript($"CREATE TRIGGER fail_seed {triggerOn} BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        using (Run first = new(db))
        {
            Assert.True(first.Inventory.Ensure(Player));
            first.Flush();

            // The one closure rolled back: no item, no slot, no equip, no grant.
            Assert.Equal(1, first.FailedWrites);
        }

        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM item_instances;"));
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM player_inventory_slots;"));
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM player_starter_grants;"));
        db.ExecuteScript("DROP TRIGGER fail_seed;");

        using Run restarted = new(db);
        Assert.True(restarted.Inventory.Ensure(Player));
        restarted.Flush();

        Assert.Equal(0, restarted.FailedWrites);
        AssertCompleteKit(db);
    }

    [Fact]
    public void ANewPlayerSeed_IsOneQueuedClosure_SoNoCommitCanLandOnlyTheFirstKitItem()
    {
        using MigrationTestDatabase db = Migrated();
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        PlayerInventoryService inventory = new(bundle.World, writes, new ObjectIdAllocator(bundle.World.LoadNextObjectId()));
        inventory.ApplyLoaded(Player, bundle.World.LoadPlayerInventory(Player));

        long before = writes.CompletedWrites;
        Assert.True(inventory.Ensure(Player));
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));

        Assert.Equal(1, writes.CompletedWrites - before);
        Assert.Equal(0, writes.FailedWrites);
        AssertCompleteKit(db);
    }

    [Fact]
    public void ALegacyInventoryWithTheWholePreKit_ButNoGrantRow_GetsOnlyTheNewerKinds()
    {
        using MigrationTestDatabase db = Migrated();
        using (Run setup = new(db))
        {
            // The pre-T2 kit as migration 0010 leaves it: old slot i at anchor 2 * i.
            IReadOnlyList<StarterKitEntry> kit = ToolCatalog.StarterKit;
            for (int old = 0; kit[old].IntroducedVersion <= ToolCatalog.UnrecordedStarterKitVersion; old++)
            {
                Give(setup.World, old * 2, 10 + old, kit[old].ItemKind);
            }
        }

        using Run run = new(db);
        Assert.True(run.Inventory.Ensure(Player));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        AssertCompleteKit(db);
    }

    [Fact]
    public void ALegacyInventoryThatIsNotAKitPrefix_ButNoGrantRow_GetsNoOldKinds()
    {
        using MigrationTestDatabase db = Migrated();
        using (Run setup = new(db))
        {
            // One maul and nothing else: not a prefix of the kit (the hammer is missing), so a pre-kit
            // player who got rid of the rest.
            Give(setup.World, 5, 10, ToolCatalog.StarterKit[2].ItemKind);
        }

        using Run run = new(db);
        Assert.True(run.Inventory.Ensure(Player));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM item_instances WHERE item_kind <> 'tool.torch';"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM item_instances WHERE item_kind = 'tool.torch';"));
        Assert.Equal((long)ToolCatalog.CurrentStarterKitVersion, db.Scalar("SELECT kit_version FROM player_starter_grants;"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ACutShortSeedOnDisk_WithNoGrantRow_GetsTheMissingKinds_WithoutDuplicates(int present)
    {
        using MigrationTestDatabase db = Migrated();
        using (Run setup = new(db))
        {
            // What the old per-item seeding left after a crash (old slots 0..k-1, which migration 0010 put
            // at anchors 0, 2, 4): the first kit items, no grant row.
            for (int index = 0; index < present; index++)
            {
                Give(setup.World, index * 2, 10 + index, ToolCatalog.StarterKit[index].ItemKind);
            }
        }

        using (Run run = new(db))
        {
            Assert.True(run.Inventory.Ensure(Player));
            run.Flush();
            Assert.Equal(0, run.FailedWrites);
        }

        AssertCompleteKit(db);
        for (int index = 0; index < present; index++)
        {
            Assert.Equal(1L, db.Scalar($"SELECT COUNT(*) FROM item_instances WHERE item_instance_id = {10 + index};"));
        }

        // A second join changes nothing.
        using Run again = new(db);
        Assert.True(again.Inventory.Ensure(Player));
        again.Flush();
        Assert.Equal((long)ToolCatalog.StarterKit.Count, db.Scalar("SELECT COUNT(*) FROM item_instances;"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void APlayerWithAGrantRow_WhoHasFewKitItems_IsNeverGivenThemAgain(int grantedVersion)
    {
        using MigrationTestDatabase db = Migrated();
        using (Run setup = new(db))
        {
            Give(setup.World, 0, 10, ToolCatalog.StarterKit[0].ItemKind);
            setup.World.SaveStarterKitGrant(Player, grantedVersion);
        }

        using Run run = new(db);
        Assert.True(run.Inventory.Ensure(Player));
        run.Flush();

        // Version 1 still receives the version-2 torch; version 2 receives nothing.
        Assert.Equal(grantedVersion == 1 ? 2L : 1L, db.Scalar("SELECT COUNT(*) FROM item_instances;"));
    }
}
