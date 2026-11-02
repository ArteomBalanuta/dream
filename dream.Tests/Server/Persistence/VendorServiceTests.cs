using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// M3 workstream B: PlayerInventoryService.TryBuy and TrySell on real SQLite through the background write
// queue. Every accepted action is one closure holding the item rows and a wallet delta, so a failed write
// leaves the rows exactly as they were, and a rejected action queues nothing.
public sealed class VendorServiceTests
{
    private const string Player = "Alice";
    private const string Kind = "tool.torch";

    private static MigrationTestDatabase Migrated()
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqliteConnection.ClearAllPools();
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('Alice', 'Alice');");
        return db;
    }

    private sealed class Run : IDisposable
    {
        private readonly ServerPersistenceBundle _bundle;
        private readonly PersistenceWriteQueue _writes = PersistenceWriteQueue.Background();

        public Run(MigrationTestDatabase db)
        {
            _bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
            Inventory = new PlayerInventoryService(_bundle.World, _writes, new ObjectIdAllocator(_bundle.World.LoadNextObjectId()));
            // An empty bag: the starter kit counts as received.
            _bundle.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
            Assert.True(Inventory.Ensure(Player));
        }

        public PlayerInventoryService Inventory { get; }

        public IWorldPersistenceRepository World => _bundle.World;

        public long FailedWrites => _writes.FailedWrites;

        public void Flush() => Assert.True(_writes.Flush(TimeSpan.FromSeconds(30)));

        public PersistedPlayerInventory Rows() => World.LoadPlayerInventory(Player);

        public void Fund(long gold)
        {
            Assert.True(Inventory.TryAddGold(Player, gold));
            Flush();
            Assert.Equal(0, FailedWrites);
        }

        public InventorySlotSnapshot OnlyBagItem() =>
            Assert.Single(Inventory.BuildSnapshot(Player).Slots, slot => slot.Item != null);

        public void Dispose()
        {
            _writes.Dispose();
            _bundle.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    private static long Price(string kind) =>
        Assert.Single(ContentCatalog.Shipped.Npcs[0].Stock, entry => entry.ItemKind == kind).Price;

    [Fact]
    public void ABuy_DebitsTheWallet_AndPutsTheItemInTheBag_AsOneWrite()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.Fund(50);
        long price = Price(Kind);

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryBuy(Player, Kind, price));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Assert.Equal(50 - price, run.Inventory.GoldOf(Player));
        Assert.Equal(50 - price, run.Inventory.BuildSnapshot(Player).Gold);
        InventorySlotSnapshot bought = run.OnlyBagItem();
        Assert.Equal(Kind, bought.Item!.Value.ItemKind);
        Assert.False(bought.Item.Value.IsRolled);
        PersistedPlayerInventory rows = run.Rows();
        Assert.Equal(50 - price, rows.Gold);
        Assert.Equal(bought.Item.Value.ItemInstanceId, Assert.Single(rows.Slots).ItemInstanceId);
        Assert.Equal(Kind, Assert.Single(rows.Items).ItemKind);
    }

    [Fact]
    public void ABuyWithTooLittleGold_ChangesAndQueuesNothing()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        long price = Price(Kind);
        run.Fund(price - 1);

        Assert.Equal(InventoryEquipOutcome.NotEnoughGold, run.Inventory.TryBuy(Player, Kind, price));
        run.Flush();

        Assert.Equal(price - 1, run.Inventory.GoldOf(Player));
        Assert.Empty(run.Inventory.BuildSnapshot(Player).Slots);
        Assert.Empty(run.Rows().Slots);
        Assert.Equal(price - 1, run.Rows().Gold);
        Assert.Equal(0, run.FailedWrites);
    }

    [Fact]
    public void ABuyIntoAFullBag_IsRefused_AndKeepsTheGold()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.Fund(1000);
        int bought = 0;
        while (run.Inventory.TryBuy(Player, Kind, Price(Kind)) == InventoryEquipOutcome.Equipped)
        {
            bought++;
            Assert.True(bought <= InventoryConstants.BackpackSlotCount);
        }

        run.Flush();
        long gold = run.Inventory.GoldOf(Player);
        Assert.Equal(1000 - (bought * Price(Kind)), gold);
        Assert.Equal(InventoryEquipOutcome.BackpackFull, run.Inventory.TryBuy(Player, Kind, Price(Kind)));
        Assert.Equal(gold, run.Inventory.GoldOf(Player));
        run.Flush();
        Assert.Equal(gold, run.Rows().Gold);
        Assert.Equal(bought, run.Rows().Slots.Count);
        Assert.Equal(0, run.FailedWrites);
    }

    [Theory]
    [InlineData("tool.nope")]
    [InlineData("gear.iron-blade")]
    [InlineData("")]
    public void ABuyOfAnUnknownOrRolledKind_IsNotInStock(string kind)
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.Fund(100);

        Assert.Equal(InventoryEquipOutcome.NotInStock, run.Inventory.TryBuy(Player, kind, 5));
        Assert.Equal(InventoryEquipOutcome.NotInStock, run.Inventory.TryBuy(Player, Kind, 0));
        Assert.Equal(InventoryEquipOutcome.NotInStock, run.Inventory.TryBuy(Player, Kind, -5));
        Assert.Equal(100, run.Inventory.GoldOf(Player));
    }

    [Fact]
    public void ASell_CreditsTheWallet_RemovesTheItem_AndASecondSellFails()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.Fund(50);
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryBuy(Player, Kind, Price(Kind)));
        InventorySlotSnapshot item = run.OnlyBagItem();
        long afterBuy = run.Inventory.GoldOf(Player);
        long sellPrice = ContentCatalog.Shipped.TryGetItem(Kind, out ItemDefinition? definition) ? definition.SellPrice : 0;

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TrySell(Player, item.SlotIndex, item.Item!.Value.ItemInstanceId));
        Assert.Equal(InventoryEquipOutcome.SlotEmpty, run.Inventory.TrySell(Player, item.SlotIndex, item.Item!.Value.ItemInstanceId));
        run.Flush();

        Assert.Equal(afterBuy + sellPrice, run.Inventory.GoldOf(Player));
        Assert.Empty(run.Inventory.BuildSnapshot(Player).Slots);
        PersistedPlayerInventory rows = run.Rows();
        Assert.Equal(afterBuy + sellPrice, rows.Gold);
        Assert.Empty(rows.Slots);
        Assert.Empty(rows.Items);
        Assert.Equal(0, run.FailedWrites);
    }

    [Fact]
    public void BuyingThenSellingBack_AlwaysLosesGold_ForEveryItemTheVendorStocks()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.Fund(10_000);
        long start = run.Inventory.GoldOf(Player);

        foreach (NpcStockEntry entry in ContentCatalog.Shipped.Npcs[0].Stock)
        {
            long before = run.Inventory.GoldOf(Player);
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryBuy(Player, entry.ItemKind, entry.Price));
            InventorySlotSnapshot item = run.Inventory.BuildSnapshot(Player).Slots.Single(slot => slot.Item?.ItemKind == entry.ItemKind);
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TrySell(Player, item.SlotIndex, item.Item!.Value.ItemInstanceId));
            Assert.True(run.Inventory.GoldOf(Player) < before, $"{entry.ItemKind} round trip must cost gold");
        }

        run.Flush();
        Assert.True(run.Inventory.GoldOf(Player) < start);
        Assert.Equal(run.Inventory.GoldOf(Player), run.Rows().Gold);
        Assert.Equal(0, run.FailedWrites);
    }

    [Fact]
    public void ARolledItem_SellsForMoreAtHigherRarity()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        long previous = 0;
        int seed = 0;
        foreach (ItemRarity rarity in new[] { ItemRarity.Common, ItemRarity.Uncommon, ItemRarity.Rare })
        {
            long before = run.Inventory.GoldOf(Player);
            Assert.Equal(
                InventoryEquipOutcome.Equipped,
                run.Inventory.TryGrantRolled(Player, "gear.leather-cap", rarity, new ItemRollSeed(1, 1, 1, 1, seed++)));
            InventorySlotSnapshot item = run.OnlyBagItem();
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TrySell(Player, item.SlotIndex, item.Item!.Value.ItemInstanceId));
            long paid = run.Inventory.GoldOf(Player) - before;
            Assert.True(paid > previous, $"{rarity} paid {paid}");
            previous = paid;
        }

        run.Flush();
        Assert.Equal(0, run.FailedWrites);
        Assert.Equal(run.Inventory.GoldOf(Player), run.Rows().Gold);
    }

    [Fact]
    public void ASellRejectsAWrongId_ANonBagIndex_AnUnsellableKind_AndAFullWallet_ChangingNothing()
    {
        using MigrationTestDatabase db = Migrated();
        // A kind the catalog no longer knows is kept as a row; it has no price.
        using (Run setup = new(db))
        {
            setup.World.SaveItemInstance(EquipmentTestWorld.MakeItem(900, Player, Kind) with { ItemKind = "tool.retired" });
            setup.World.SaveInventorySlot(new PersistedInventorySlot(Player, 20, 900, DateTime.UtcNow));
        }

        using Run run = new(db);
        Assert.Equal(InventoryEquipOutcome.NotSellable, run.Inventory.TrySell(Player, 20, 900));
        Assert.Equal(InventoryEquipOutcome.ItemMismatch, run.Inventory.TrySell(Player, 20, 901));
        Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, run.Inventory.TrySell(Player, EquipmentSlots.FirstSlotIndex, 900));
        Assert.Equal(InventoryEquipOutcome.SlotOutOfRange, run.Inventory.TrySell(Player, -1, 900));
        Assert.Equal(InventoryEquipOutcome.SlotEmpty, run.Inventory.TrySell(Player, 5, 900));

        // A full wallet cannot take the price.
        run.Fund(InventoryConstants.MaxGold);
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryBuy(Player, Kind, Price(Kind)));
        InventorySlotSnapshot item = run.Inventory.BuildSnapshot(Player).Slots.Single(slot => slot.Item?.ItemKind == Kind);
        Assert.True(run.Inventory.TryAddGold(Player, Price(Kind)));
        Assert.Equal(InventoryConstants.MaxGold, run.Inventory.GoldOf(Player));
        Assert.Equal(InventoryEquipOutcome.WalletFull, run.Inventory.TrySell(Player, item.SlotIndex, item.Item!.Value.ItemInstanceId));
        Assert.Equal(InventoryConstants.MaxGold, run.Inventory.GoldOf(Player));
        Assert.Contains(run.Inventory.BuildSnapshot(Player).Slots, slot => slot.Item?.ItemInstanceId == item.Item!.Value.ItemInstanceId);
    }

    [Fact]
    public void ARejectedAction_NeverSpendsTheBudget_AndAThrottledOneChangesNothing()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.Fund(50);
        int spent = 0;
        bool allow = true;
        run.Inventory.ActionGate = () =>
        {
            spent++;
            return allow;
        };

        Assert.Equal(InventoryEquipOutcome.NotEnoughGold, run.Inventory.TryBuy(Player, "tool.survey-lantern", 9999));
        Assert.Equal(InventoryEquipOutcome.NotInStock, run.Inventory.TryBuy(Player, "tool.nope", 5));
        Assert.Equal(InventoryEquipOutcome.SlotEmpty, run.Inventory.TrySell(Player, 3, 1));
        Assert.Equal(0, spent);

        allow = false;
        Assert.Equal(InventoryEquipOutcome.Throttled, run.Inventory.TryBuy(Player, Kind, Price(Kind)));
        Assert.Equal(1, spent);
        Assert.Equal(50, run.Inventory.GoldOf(Player));
        Assert.Empty(run.Inventory.BuildSnapshot(Player).Slots);
    }

    [Fact]
    public void AFailedBuyWrite_LeavesTheRowsUntouched_AndMemoryKeepsGoldAndItemTogether()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.Fund(50);
        long price = Price(Kind);
        db.ExecuteScript("CREATE TRIGGER fail_wallet BEFORE UPDATE ON player_wallets BEGIN SELECT RAISE(ABORT, 'simulated'); END;");

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryBuy(Player, Kind, price));
        run.Flush();

        // The closure rolled back as one: no item row, no slot row, the old gold.
        Assert.Equal(1, run.FailedWrites);
        PersistedPlayerInventory rows = run.Rows();
        Assert.Equal(50, rows.Gold);
        Assert.Empty(rows.Slots);
        Assert.Empty(rows.Items);
        // Memory has both halves of the trade (the failed write retains the inventory).
        Assert.Equal(50 - price, run.Inventory.GoldOf(Player));
        Assert.Single(run.Inventory.BuildSnapshot(Player).Slots, slot => slot.Item?.ItemKind == Kind);
    }

    [Fact]
    public void AFailedSellWrite_LeavesTheItemAndTheGoldOfTheRows_NeverOneWithoutTheOther()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.Fund(50);
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryBuy(Player, Kind, Price(Kind)));
        run.Flush();
        InventorySlotSnapshot item = run.OnlyBagItem();
        long goldInRows = run.Rows().Gold;
        db.ExecuteScript("CREATE TRIGGER fail_wallet BEFORE UPDATE ON player_wallets BEGIN SELECT RAISE(ABORT, 'simulated'); END;");

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TrySell(Player, item.SlotIndex, item.Item!.Value.ItemInstanceId));
        run.Flush();

        Assert.Equal(1, run.FailedWrites);
        PersistedPlayerInventory rows = run.Rows();
        Assert.Equal(goldInRows, rows.Gold);
        Assert.Equal(item.Item!.Value.ItemInstanceId, Assert.Single(rows.Slots).ItemInstanceId);
        Assert.Single(rows.Items);
        // Memory has both halves of the sale.
        Assert.Equal(goldInRows + ContentCatalog.Shipped.Items.Single(i => i.Key == Kind).SellPrice, run.Inventory.GoldOf(Player));
        Assert.Empty(run.Inventory.BuildSnapshot(Player).Slots);
    }

    [Fact]
    public void AFailureAfterTheWalletStep_OfASell_RollsTheCreditBack()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        run.Fund(50);
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryBuy(Player, Kind, Price(Kind)));
        run.Flush();
        InventorySlotSnapshot item = run.OnlyBagItem();
        long goldInRows = run.Rows().Gold;
        db.ExecuteScript("CREATE TRIGGER fail_delete BEFORE DELETE ON item_instances BEGIN SELECT RAISE(ABORT, 'simulated'); END;");

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TrySell(Player, item.SlotIndex, item.Item!.Value.ItemInstanceId));
        run.Flush();

        Assert.Equal(1, run.FailedWrites);
        Assert.Equal(goldInRows, run.Rows().Gold);
        Assert.Single(run.Rows().Items);
    }
}
