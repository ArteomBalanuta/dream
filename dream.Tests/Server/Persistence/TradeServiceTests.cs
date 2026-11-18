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

// M5 workstream A: PlayerInventoryService.TryExecuteTrade on real SQLite through the background write
// queue. One closure holds the whole exchange (debits, deletes, guarded inserts, credits), so a write that
// fails leaves the rows exactly as they were and no item or gold appears from nowhere; the in-memory
// fingerprint a trade watches moves on every inventory change, whichever route made it.
public sealed class TradeServiceTests
{
    private const string Alice = "Alice";
    private const string Bob = "Bob";
    private const string Band = "gear.iron-band";

    private static MigrationTestDatabase Migrated()
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqlitePools.Release(db.DatabasePath);
        db.ExecuteScript(
            "INSERT INTO player_profiles (player_name, display_name) VALUES ('Alice', 'Alice');" +
            "INSERT INTO player_profiles (player_name, display_name) VALUES ('Bob', 'Bob');");
        return db;
    }

    private sealed class Run : IDisposable
    {
        private readonly ServerPersistenceBundle _bundle;
        private readonly string _databasePath;
        private int _seed;

        public Run(MigrationTestDatabase db)
        {
            _databasePath = db.DatabasePath;
            _bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
            Writes = PersistenceWriteQueue.Background();
            Inventory = new PlayerInventoryService(_bundle.World, Writes, new ObjectIdAllocator(_bundle.World.LoadNextObjectId()));
            foreach (string player in new[] { Alice, Bob })
            {
                // An empty bag: the starter kit counts as received.
                _bundle.World.SaveStarterKitGrant(player, ToolCatalog.CurrentStarterKitVersion);
                Assert.True(Inventory.Ensure(player));
            }
        }

        public PersistenceWriteQueue Writes { get; }

        public PlayerInventoryService Inventory { get; }

        public IWorldPersistenceRepository World => _bundle.World;

        public long FailedWrites => Writes.FailedWrites;

        public void Flush() => Assert.True(Writes.Flush(TimeSpan.FromSeconds(30)));

        public void Fund(string player, long gold)
        {
            Assert.True(Inventory.TryAddGold(player, gold));
            Flush();
        }

        // A new 1x1 item in the bag; its id.
        public long Grant(string player)
        {
            Assert.Equal(
                InventoryEquipOutcome.Equipped,
                Inventory.TryGrantRolled(player, Band, ItemRarity.Common, new ItemRollSeed(5, 5, 5, 5, _seed++)));
            return Inventory.BuildSnapshot(player).AllItems().Max(item => item.ItemInstanceId);
        }

        public long[] BagIds(string player) =>
            [.. Inventory.BuildSnapshot(player).BagItems().Select(slot => slot.Item!.Value.ItemInstanceId).Order()];

        public long[] RowIds(string player) =>
            [.. World.LoadPlayerInventory(player).Slots.Select(slot => slot.ItemInstanceId).Order()];

        public void Dispose()
        {
            Writes.Dispose();
            _bundle.Dispose();
            SqlitePools.Release(_databasePath);
        }
    }

    private static string OwnerRow(MigrationTestDatabase db, long itemId) =>
        Convert.ToString(db.Scalar($"SELECT COALESCE(owner_player_name, '') FROM item_instances WHERE item_instance_id = {itemId}"))!;

    [Fact]
    public void ASwap_MovesItemsAndGoldBothWays_InMemoryAndInTheRows_WithoutDuplicates()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        long a1 = run.Grant(Alice);
        long a2 = run.Grant(Alice);
        long b1 = run.Grant(Bob);
        run.Fund(Alice, 500);
        run.Fund(Bob, 70);

        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            run.Inventory.TryExecuteTrade(Alice, [a1, a2], 200, Bob, [b1], 20));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Assert.Equal([b1], run.BagIds(Alice));
        Assert.Equal(new[] { a1, a2 }.Order(), run.BagIds(Bob));
        Assert.Equal(500 - 200 + 20, run.Inventory.GoldOf(Alice));
        Assert.Equal(70 - 20 + 200, run.Inventory.GoldOf(Bob));
        PersistedPlayerInventory aliceRows = run.World.LoadPlayerInventory(Alice);
        PersistedPlayerInventory bobRows = run.World.LoadPlayerInventory(Bob);
        Assert.Equal([b1], run.RowIds(Alice));
        Assert.Equal(new[] { a1, a2 }.Order(), run.RowIds(Bob));
        Assert.Equal(500 - 200 + 20, aliceRows.Gold);
        Assert.Equal(70 - 20 + 200, bobRows.Gold);
        Assert.Equal(Alice, OwnerRow(db, b1));
        Assert.Equal(Bob, OwnerRow(db, a1));
        Assert.Equal(Bob, OwnerRow(db, a2));
        Assert.Equal(0, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM (SELECT 1 FROM player_inventory_slots GROUP BY world_id, item_instance_id HAVING COUNT(*) > 1)")));
    }

    [Fact]
    public void AGiftOfGoldAlone_AndAnItemForNothing_BothWork()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        long a1 = run.Grant(Alice);
        run.Fund(Alice, 50);

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryExecuteTrade(Alice, [], 50, Bob, [], 0));
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryExecuteTrade(Alice, [a1], 0, Bob, [], 0));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Assert.Equal(0, run.Inventory.GoldOf(Alice));
        Assert.Equal(50, run.Inventory.GoldOf(Bob));
        Assert.Equal(50, run.World.LoadPlayerInventory(Bob).Gold);
        Assert.Equal([a1], run.RowIds(Bob));
        Assert.Empty(run.RowIds(Alice));
    }

    [Fact]
    public void HostileOffers_AreRefusedBeforeAnythingChanges_AndQueueNothing()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        long a1 = run.Grant(Alice);
        long b1 = run.Grant(Bob);
        run.Fund(Alice, 100);
        run.Fund(Bob, 100);
        long[] nine = [.. Enumerable.Range(1, 9).Select(n => (long)n)];

        // A foreign id (Bob's item offered by Alice), and an id no one holds.
        Assert.Equal(InventoryEquipOutcome.ItemGone, run.Inventory.TryExecuteTrade(Alice, [b1], 0, Bob, [], 0));
        Assert.Equal(InventoryEquipOutcome.ItemGone, run.Inventory.TryExecuteTrade(Alice, [999_999], 0, Bob, [], 0));
        // The same id on both sides.
        Assert.Equal(InventoryEquipOutcome.ItemGone, run.Inventory.TryExecuteTrade(Alice, [a1], 0, Bob, [a1], 0));
        // A duplicate in one list, a non-positive id, nine items, a missing list.
        Assert.Equal(InventoryEquipOutcome.TradeUnavailable, run.Inventory.TryExecuteTrade(Alice, [a1, a1], 0, Bob, [], 0));
        Assert.Equal(InventoryEquipOutcome.TradeUnavailable, run.Inventory.TryExecuteTrade(Alice, [0], 0, Bob, [], 0));
        Assert.Equal(InventoryEquipOutcome.TradeUnavailable, run.Inventory.TryExecuteTrade(Alice, nine, 0, Bob, [], 0));
        Assert.Equal(InventoryEquipOutcome.TradeUnavailable, run.Inventory.TryExecuteTrade(Alice, null!, 0, Bob, [], 0));
        // Gold: negative, over the wallet, over MaxGold.
        Assert.Equal(InventoryEquipOutcome.TradeUnavailable, run.Inventory.TryExecuteTrade(Alice, [], -1, Bob, [], 0));
        Assert.Equal(InventoryEquipOutcome.NotEnoughGold, run.Inventory.TryExecuteTrade(Alice, [], 101, Bob, [], 0));
        Assert.Equal(InventoryEquipOutcome.TradeUnavailable, run.Inventory.TryExecuteTrade(Alice, [], InventoryConstants.MaxGold + 1, Bob, [], 0));
        // A trade with oneself.
        Assert.Equal(InventoryEquipOutcome.TradeUnavailable, run.Inventory.TryExecuteTrade(Alice, [a1], 0, Alice, [], 0));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Assert.Equal([a1], run.BagIds(Alice));
        Assert.Equal([b1], run.BagIds(Bob));
        Assert.Equal(100, run.Inventory.GoldOf(Alice));
        Assert.Equal(100, run.Inventory.GoldOf(Bob));
        Assert.Equal([a1], run.RowIds(Alice));
        Assert.Equal([b1], run.RowIds(Bob));
    }

    [Fact]
    public void AWornItem_CannotBeOffered()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        long band = run.Grant(Alice);
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryEquip(Alice, run.Inventory.BuildSnapshot(Alice).AnchorOfId(band)));
        run.Flush();
        Assert.Empty(run.Inventory.BuildSnapshot(Alice).BagItems());

        Assert.Equal(InventoryEquipOutcome.ItemGone, run.Inventory.TryExecuteTrade(Alice, [band], 0, Bob, [], 0));
        Assert.Empty(run.BagIds(Bob));
    }

    [Fact]
    public void AWalletThatCannotTakeTheGold_AndABagThatCannotTakeTheItems_AreRefusedWhole()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        long a1 = run.Grant(Alice);
        run.Fund(Alice, 100);
        run.Fund(Bob, InventoryConstants.MaxGold - 5);

        Assert.Equal(InventoryEquipOutcome.WalletFull, run.Inventory.TryExecuteTrade(Alice, [a1], 10, Bob, [], 0));
        // The recipient's own outgoing gold makes room: Bob sends 20 back, so 10 fits.
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryExecuteTrade(Alice, [], 10, Bob, [], 20));
        Assert.Equal(110, run.Inventory.GoldOf(Alice));

        run.Inventory.FillBagLeaving(Bob, freeCells: 0);
        Assert.Equal(InventoryEquipOutcome.BackpackFull, run.Inventory.TryExecuteTrade(Alice, [a1], 0, Bob, [], 0));
        Assert.Equal([a1], run.BagIds(Alice));
        run.Flush();
        Assert.Equal(0, run.FailedWrites);
    }

    [Fact]
    public void ASwapIntoFullBags_Fits_BecauseEachSideLeavesItsOwnCellsFirst()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.Inventory.FillBagLeaving(Alice, freeCells: 0);
        run.Inventory.FillBagLeaving(Bob, freeCells: 0);
        long a1 = run.BagIds(Alice)[0];
        long b1 = run.BagIds(Bob)[0];
        run.Flush();

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryExecuteTrade(Alice, [a1], 0, Bob, [b1], 0));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Assert.Contains(b1, run.BagIds(Alice));
        Assert.DoesNotContain(a1, run.BagIds(Alice));
        Assert.Contains(a1, run.RowIds(Bob));
        Assert.Contains(b1, run.RowIds(Alice));
        Assert.Equal(64, run.RowIds(Alice).Length);
        Assert.Equal(64, run.RowIds(Bob).Length);
        Assert.True(run.Inventory.BuildSnapshot(Alice).BoardIsConsistent());
    }

    [Theory]
    [InlineData("CREATE TRIGGER fail_wallet BEFORE UPDATE ON player_wallets BEGIN SELECT RAISE(ABORT, 'simulated'); END;")]
    [InlineData("CREATE TRIGGER fail_slots BEFORE INSERT ON player_inventory_slots BEGIN SELECT RAISE(ABORT, 'simulated'); END;")]
    [InlineData("CREATE TRIGGER fail_owner BEFORE UPDATE ON item_instances BEGIN SELECT RAISE(ABORT, 'simulated'); END;")]
    public void AWriteThatFails_RollsTheWholeExchangeBack_PausesBothSides_AndTheRowsKeepTheirOwners(string trigger)
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        long a1 = run.Grant(Alice);
        long b1 = run.Grant(Bob);
        run.Fund(Alice, 100);
        run.Fund(Bob, 40);
        db.ExecuteScript(trigger);

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryExecuteTrade(Alice, [a1], 30, Bob, [b1], 10));
        run.Flush();

        // The closure failed as a whole: the rows are untouched.
        Assert.Equal(1, run.FailedWrites);
        Assert.Equal([a1], run.RowIds(Alice));
        Assert.Equal([b1], run.RowIds(Bob));
        Assert.Equal(100, run.World.LoadPlayerInventory(Alice).Gold);
        Assert.Equal(40, run.World.LoadPlayerInventory(Bob).Gold);
        Assert.Equal(Alice, OwnerRow(db, a1));
        Assert.Equal(Bob, OwnerRow(db, b1));

        // Both sides saw a write queued, so both are paused: any later trade is refused at the gate, and
        // the rows are still the original ones (the memory copy is the only current one until a restart).
        Assert.Equal(InventoryEquipOutcome.WriteFailed, run.Inventory.TryExecuteTrade(Alice, [], 1, Bob, [], 0));
        Assert.Equal(InventoryEquipOutcome.WriteFailed, run.Inventory.ValidateTradeOffer(Alice, [], 0));
        Assert.Equal(InventoryEquipOutcome.WriteFailed, run.Inventory.ValidateTradeOffer(Bob, [], 0));
        run.Flush();
        Assert.Equal(1, run.FailedWrites);
    }

    [Fact]
    public void APriorFailedWrite_OnEitherSide_RefusesTheTrade()
    {
        foreach (string failer in new[] { Alice, Bob })
        {
            using MigrationTestDatabase db = Migrated();
            using Run run = new(db);

            // One failed gold credit of the chosen player; the other has never queued a write, so it has no
            // failure of its own on record: the gate must still close on the failed side alone.
            db.ExecuteScript("CREATE TRIGGER fail_wallet BEFORE INSERT ON player_wallets BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
            Assert.True(run.Inventory.TryAddGold(failer, 5));
            run.Flush();
            Assert.Equal(1, run.FailedWrites);
            db.ExecuteScript("DROP TRIGGER fail_wallet;");

            string other = failer == Alice ? Bob : Alice;
            Assert.Equal(InventoryEquipOutcome.WriteFailed, run.Inventory.ValidateTradeOffer(failer, [], 1));
            Assert.Equal(InventoryEquipOutcome.WriteFailed, run.Inventory.CheckTradeGate(failer));
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.CheckTradeGate(other));
            Assert.Equal(InventoryEquipOutcome.WriteFailed, run.Inventory.TryExecuteTrade(failer, [], 1, other, [], 0));
            Assert.Equal(InventoryEquipOutcome.WriteFailed, run.Inventory.TryExecuteTrade(other, [], 0, failer, [], 1));
            run.Flush();
            Assert.Equal(1, run.FailedWrites);
            Assert.Equal(0, run.World.LoadPlayerInventory(other).Gold);
        }
    }

    [Fact]
    public void APhantomItemFromAFailedBuy_CannotBeTraded_TheDeleteFirstGuardThrowsAndTheGoldStays()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.Fund(Alice, 100);
        run.Fund(Bob, 100);

        // Hold the write worker, so the failing buy is still queued when the trade is checked: the gate
        // (a failure that already happened) cannot see it, only the guards inside the closure can.
        using ManualResetEventSlim release = new(false);
        run.Writes.Enqueue(() => release.Wait(TimeSpan.FromSeconds(30)));
        db.ExecuteScript("CREATE TRIGGER fail_wallet BEFORE UPDATE ON player_wallets BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryBuy(Alice, "tool.torch", 10));
        long phantom = run.Inventory.BuildSnapshot(Alice).AllItems().Single().ItemInstanceId;

        // The gate is open (nothing failed yet) and the trade is queued behind the buy.
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryExecuteTrade(Alice, [phantom], 5, Bob, [], 0));
        release.Set();
        run.Flush();

        // The buy failed (its wallet debit), the trade failed on its first detach: neither left a row, and
        // the trade's gold debit rolled back with it.
        Assert.Equal(2, run.FailedWrites);
        Assert.Empty(run.RowIds(Alice));
        Assert.Empty(run.RowIds(Bob));
        Assert.Equal(100, run.World.LoadPlayerInventory(Alice).Gold);
        Assert.Equal(100, run.World.LoadPlayerInventory(Bob).Gold);
        Assert.Equal(0, Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM item_instances WHERE item_instance_id = {phantom}")));
    }

    [Fact]
    public void TheRepositoryGuards_DetachThrowsWithoutARow_AttachThrowsOnAnOccupiedSlot_AndNeverOverwrites()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        long a1 = run.Grant(Alice);
        long b1 = run.Grant(Bob);
        run.Flush();

        // No row for that player.
        Assert.Throws<InvalidOperationException>(() => run.World.DetachHeldItem(Alice, b1));
        // An item with no item row to hand over.
        Assert.Throws<InvalidOperationException>(() => run.World.AttachItem(Alice, 424_242, 5, DateTime.UtcNow));
        // Detach leaves the item row; the attach into an occupied slot throws and changes nothing.
        run.World.DetachHeldItem(Alice, a1);
        Assert.Empty(run.RowIds(Alice));
        Assert.Equal(1, Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM item_instances WHERE item_instance_id = {a1}")));
        int bobSlot = run.World.LoadPlayerInventory(Bob).Slots.Single().SlotIndex;
        Assert.Throws<SqliteException>(() => run.World.AttachItem(Bob, a1, bobSlot, DateTime.UtcNow));
        Assert.Equal([b1], run.RowIds(Bob));
        run.World.AttachItem(Bob, a1, bobSlot == 0 ? 1 : 0, DateTime.UtcNow);
        Assert.Equal(new[] { a1, b1 }.Order(), run.RowIds(Bob));
        Assert.Equal(Bob, OwnerRow(db, a1));
    }

    [Fact]
    public void EveryRouteThatChangesTheBagOrTheWallet_ChangesTheFingerprintAnOpenTradeWatches()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        PlayerInventoryService inv = run.Inventory;
        long fingerprint = inv.TradeFingerprint(Alice);
        Assert.NotEqual(0, fingerprint);

        void Changed(string route)
        {
            long now = inv.TradeFingerprint(Alice);
            Assert.True(now != fingerprint, $"{route} left the fingerprint as it was");
            fingerprint = now;
        }

        Assert.True(inv.TryAddGold(Alice, 100));
        Changed("gold credit");
        Assert.Equal(InventoryEquipOutcome.Equipped, inv.TryBuy(Alice, "tool.torch", 10));
        Changed("buy");
        long torch = inv.BuildSnapshot(Alice).AllItems().Single().ItemInstanceId;
        Assert.Equal(InventoryEquipOutcome.Equipped, inv.TryGrantRolled(Alice, Band, ItemRarity.Common, new ItemRollSeed(1, 2, 3, 4, 5)));
        Changed("grant");
        long band = inv.BuildSnapshot(Alice).AllItems().Single(item => item.ItemKind == Band).ItemInstanceId;
        Assert.Equal(InventoryEquipOutcome.Equipped, inv.TryMove(Alice, inv.BuildSnapshot(Alice).AnchorOfId(torch), 40));
        Changed("move");
        Assert.Equal(InventoryEquipOutcome.Equipped, inv.TryEquip(Alice, inv.BuildSnapshot(Alice).AnchorOfId(band)));
        Changed("equip");
        PlayerInventorySnapshot worn = inv.BuildSnapshot(Alice);
        int wornSlot = worn.Equipment!.Single(slot => slot.Item?.ItemInstanceId == band).SlotIndex;
        Assert.Equal(InventoryEquipOutcome.Equipped, inv.TryUnequip(Alice, wornSlot));
        Changed("unequip");
        Assert.Equal(InventoryEquipOutcome.Equipped, inv.TrySell(Alice, inv.BuildSnapshot(Alice).AnchorOfId(band), band));
        Changed("sell");
        Assert.Equal(InventoryEquipOutcome.Equipped, inv.TryDestroy(Alice, inv.BuildSnapshot(Alice).AnchorOfId(torch), torch));
        Changed("destroy");

        // A change of the other player leaves this player's fingerprint alone.
        long before = inv.TradeFingerprint(Alice);
        Assert.True(inv.TryAddGold(Bob, 5));
        Assert.Equal(before, inv.TradeFingerprint(Alice));
        run.Flush();
    }
}
