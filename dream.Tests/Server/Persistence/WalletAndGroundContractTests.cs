using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// M3 contract: the wallet deltas and the ground-item queries of the repositories, and the service's gold
// (credit, snapshot, load, eviction), on real SQLite.
public sealed class WalletAndGroundContractTests
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

        public void Dispose()
        {
            _writes.Dispose();
            _bundle.Dispose();
            SqlitePools.Release(_databasePath);
        }
    }

    private static PersistedItemInstance Item(long id, string state, Vector3? position = null)
        => EquipmentTestWorld.MakeItem(id, Player, "tool.torch") with
        {
            State = state,
            OwnerPlayerName = state == "Ground" ? null : Player,
            WorldPosition = position
        };

    [Fact]
    public void WalletDeltas_AddUp_AndLoadWithTheInventory()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);

        Assert.Equal(0L, run.World.LoadPlayerInventory(Player).Gold);
        run.World.ApplyWalletDelta(Player, 5);
        run.World.ApplyWalletDelta(Player, 7);
        run.World.ApplyWalletDelta(Player, -2);

        Assert.Equal(10L, run.World.LoadPlayerInventory(Player).Gold);
        Assert.Equal(0L, run.World.LoadPlayerInventory("Nobody").Gold);
    }

    [Fact]
    public void ADeltaBelowZero_OrOnAMissingWallet_FailsTheStatement()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.World.ApplyWalletDelta(Player, 3);

        Assert.Throws<SqliteException>(() => run.World.ApplyWalletDelta(Player, -4));
        Assert.Throws<SqliteException>(() => run.World.ApplyWalletDelta("Bob", -1));

        Assert.Equal(3L, run.World.LoadPlayerInventory(Player).Gold);
        run.World.ApplyWalletDelta(Player, -3);
        Assert.Equal(0L, run.World.LoadPlayerInventory(Player).Gold);
    }

    [Fact]
    public void LoadGroundItems_ReturnsGroundRowsNoSlotReferences_AndDeleteGroundItemSkipsTheRest()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.World.SaveItemInstance(Item(10, "Ground", new Vector3(1f, 2f, 3f)));
        run.World.SaveItemInstance(Item(11, "Ground", new Vector3(4f, 5f, 6f)));
        run.World.SaveItemInstance(Item(12, "Inventory"));
        run.World.SaveInventorySlot(new PersistedInventorySlot(Player, 0, 12, DateTime.UtcNow));
        // A row that says Ground but a slot still references (a crash between the two writes of a pickup).
        run.World.SaveItemInstance(Item(13, "Ground", new Vector3(7f, 8f, 9f)));
        run.World.SaveInventorySlot(new PersistedInventorySlot(Player, 1, 13, DateTime.UtcNow));

        IReadOnlyList<PersistedItemInstance> ground = run.World.LoadGroundItems();

        Assert.Equal([10L, 11L], ground.Select(item => item.ItemInstanceId).ToArray());
        Assert.Equal(new Vector3(1f, 2f, 3f), ground[0].WorldPosition);
        Assert.Null(ground[0].OwnerPlayerName);

        run.World.DeleteGroundItem(12);
        run.World.DeleteGroundItem(10);

        Assert.Equal([11L], run.World.LoadGroundItems().Select(item => item.ItemInstanceId).ToArray());
        Assert.Equal(12L, Assert.Single(run.World.LoadPlayerInventory(Player).Slots, slot => slot.SlotIndex == 0).ItemInstanceId);
    }

    [Fact]
    public void TheServiceCredit_UpdatesTheSnapshot_PersistsAsADelta_AndSurvivesALoad()
    {
        using MigrationTestDatabase db = Migrated();
        using (Run run = new(db))
        {
            run.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
            Assert.True(run.Inventory.Ensure(Player));
            Assert.Equal(0L, run.Inventory.GoldOf(Player));

            Assert.True(run.Inventory.TryAddGold(Player, 40));
            Assert.True(run.Inventory.TryAddGold(Player, 2));

            Assert.Equal(42L, run.Inventory.GoldOf(Player));
            Assert.Equal(42L, run.Inventory.BuildSnapshot(Player).Gold);
            run.Flush();
            Assert.Equal(0, run.FailedWrites);
            Assert.Equal(42L, run.World.LoadPlayerInventory(Player).Gold);
        }

        using Run again = new(db);
        again.Inventory.ApplyLoaded(Player, again.World.LoadPlayerInventory(Player));
        Assert.Equal(42L, again.Inventory.GoldOf(Player));
        Assert.Equal(42L, again.Inventory.BuildSnapshot(Player).Gold);
    }

    [Fact]
    public void TheServiceCredit_RefusesZeroNegativeAnUnloadedPlayerAndAWalletOverTheCap()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        Assert.False(run.Inventory.TryAddGold(Player, 5));
        run.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        Assert.True(run.Inventory.Ensure(Player));

        Assert.False(run.Inventory.TryAddGold(Player, 0));
        Assert.False(run.Inventory.TryAddGold(Player, -3));
        Assert.True(run.Inventory.TryAddGold(Player, InventoryConstants.MaxGold - 1));
        Assert.False(run.Inventory.TryAddGold(Player, 2));
        Assert.True(run.Inventory.TryAddGold(Player, 1));
        Assert.False(run.Inventory.TryAddGold(Player, 1));

        Assert.Equal(InventoryConstants.MaxGold, run.Inventory.GoldOf(Player));
        run.Flush();
        Assert.Equal(0, run.FailedWrites);
        Assert.Equal(InventoryConstants.MaxGold, run.World.LoadPlayerInventory(Player).Gold);
    }

    [Fact]
    public void AFailedWalletWrite_RetainsTheInventoryAndItsGoldThroughEviction()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
        Assert.True(run.Inventory.Ensure(Player));
        run.Flush();
        db.ExecuteScript("CREATE TRIGGER fail_wallet BEFORE INSERT ON player_wallets BEGIN SELECT RAISE(ABORT, 'simulated'); END;");

        Assert.True(run.Inventory.TryAddGold(Player, 9));
        run.Flush();
        Assert.Equal(1, run.FailedWrites);
        run.Inventory.Evict(Player);

        // A failed write retains the inventory, gold included; the rows know nothing of it.
        Assert.Equal(9L, run.Inventory.GoldOf(Player));
        Assert.Equal(0L, run.World.LoadPlayerInventory(Player).Gold);
    }
}
