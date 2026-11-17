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

// M3 contract: migration 0012 gives an item at most one slot row in the world (unique index on
// (world_id, item_instance_id)). Every swap writes an item to a new slot while another action in the same
// closure frees its old one, so the closure must delete the old rows before it upserts the new ones.
// These run each swap path against real SQLite on a background queue (a failed closure is dropped and
// counted in FailedWrites) and require no failed write and the swapped rows.
public sealed class InventorySwapUniqueIndexTests
{
    private const string Player = "Alice";

    private static MigrationTestDatabase Migrated()
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqlitePools.Release(db.DatabasePath);
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('Alice', 'Alice');");
        return db;
    }

    private sealed class Run : IDisposable
    {
        private readonly ServerPersistenceBundle _bundle;
        private readonly string _databasePath;
        private readonly PersistenceWriteQueue _writes = PersistenceWriteQueue.Background();

        public Run(MigrationTestDatabase db)
        {
            _databasePath = db.DatabasePath;
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
            SqlitePools.Release(_databasePath);
        }
    }

    private static void Give(IWorldPersistenceRepository world, int slot, long id, string kind)
    {
        world.SaveItemInstance(EquipmentTestWorld.MakeItem(id, Player, kind));
        world.SaveInventorySlot(new PersistedInventorySlot(Player, slot, id, DateTime.UtcNow));
    }

    private static Run Seeded(MigrationTestDatabase db, params (int Slot, long Id, string Kind)[] rows)
    {
        using (Run setup = new(db))
        {
            foreach ((int slot, long id, string kind) in rows)
            {
                Give(setup.World, slot, id, kind);
            }

            setup.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        }

        Run run = new(db);
        Assert.True(run.Inventory.Ensure(Player));
        run.Flush();
        return run;
    }

    [Fact]
    public void TheIndex_RefusesASecondRowForOneItem_AndSaysSo()
    {
        using MigrationTestDatabase db = Migrated();
        using Run setup = new(db);
        Give(setup.World, 0, 10, "tool.torch");

        Assert.Throws<SqliteException>(() =>
            setup.World.SaveInventorySlot(new PersistedInventorySlot(Player, 5, 10, DateTime.UtcNow)));

        Assert.Equal(1, setup.Rows().Count);
    }

    [Fact]
    public void ABagSwap_TradesTheTwoAnchors_WithNoFailedWrite()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = Seeded(db, (0, 10, "tool.torch"), (1, 11, "tool.torch"));

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryMove(Player, 0, 1));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Dictionary<int, long> rows = run.Rows();
        Assert.Equal(11, rows[0]);
        Assert.Equal(10, rows[1]);
    }

    [Fact]
    public void EquippingOverAHeldMainHand_SendsTheDisplacedItemToTheBag_WithNoFailedWrite()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = Seeded(db, (0, 10, "tool.torch"), (EquipmentSlots.FirstSlotIndex, 11, "tool.field-blade"));

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, 0));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Dictionary<int, long> rows = run.Rows();
        Assert.Equal(10, rows[EquipmentSlots.FirstSlotIndex]);
        Assert.Equal(11, rows[0]);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ASwapBetweenTwoWornSlots_TradesThem_WithNoFailedWrite()
    {
        using MigrationTestDatabase db = Migrated();
        int mainHand = EquipmentSlots.FirstSlotIndex;
        int offHand = mainHand + (int)EquipmentSlot.OffHand;
        using Run run = Seeded(db, (mainHand, 10, "tool.torch"), (offHand, 11, "tool.survey-lantern"));

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Player, mainHand, offHand));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Dictionary<int, long> rows = run.Rows();
        Assert.Equal(10, rows[offHand]);
        Assert.Equal(11, rows[mainHand]);
    }

    [Fact]
    public void ASwapThenAnotherSwap_InOneRun_KeepsEveryRowUnique()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = Seeded(db, (0, 10, "tool.torch"), (1, 11, "tool.torch"), (2, 12, "tool.torch"));

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryMove(Player, 0, 1));
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryMove(Player, 1, 2));
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryMove(Player, 0, 1));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Dictionary<int, long> rows = run.Rows();
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows.Values.Distinct().Count());
    }
}
