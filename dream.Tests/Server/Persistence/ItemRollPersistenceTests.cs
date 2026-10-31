using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Stats;
using Dream.Tests.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// Wave H2: a rolled item's stored roll round-trips with equal float bits, never changes after the item is
// created, and reads inert when its text is damaged. Slot and item deletes for unequip and destroy.
public sealed class ItemRollPersistenceTests
{
    private const string Player = "p1";

    private static readonly ItemRoll Roll = new(
        ItemRarity.Rare,
        [
            new StatModifier(StatId.Strength, StatOp.Flat, 6f),
            new StatModifier(StatId.AttackSpeed, StatOp.Flat, 1.7000000476837158f),
            new StatModifier(StatId.Armor, StatOp.Flat, 4f)
        ]);

    private static MigrationTestDatabase Migrated()
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqliteConnection.ClearAllPools();
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('p1', 'P1'), ('p2', 'P2');");
        return db;
    }

    private static (SqliteItemRepository Items, SqliteInventoryRepository Inventory) Repositories(MigrationTestDatabase db)
    {
        SqliteWorldStore store = new(db.Schema);
        return (new SqliteItemRepository(store), new SqliteInventoryRepository(store));
    }

    private static PersistedItemInstance Item(long id, string kind, ItemRoll? roll = null) =>
        new(id, kind, "Item", "v", Player, null, "Inventory", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), default, roll);

    private static void Give(SqliteItemRepository items, SqliteInventoryRepository inventory, string player, int slot, PersistedItemInstance item)
    {
        items.SaveItemInstance(item);
        inventory.SaveInventorySlot(new PersistedInventorySlot(player, slot, item.ItemInstanceId, default));
    }

    [Fact]
    public void ARolledItem_RoundTripsWithEqualFloatBits_ThroughBothLoads()
    {
        using MigrationTestDatabase db = Migrated();
        (SqliteItemRepository items, SqliteInventoryRepository inventory) = Repositories(db);
        Give(items, inventory, Player, 0, Item(10, "gear.iron-blade", Roll));

        ItemRoll? viaInventory = Assert.Single(inventory.LoadPlayerInventory(Player).Items).Roll;
        ItemRoll? viaItems = Assert.Single(items.LoadItemInstances()).Roll;

        foreach (ItemRoll? loaded in new[] { viaInventory, viaItems })
        {
            Assert.NotNull(loaded);
            Assert.False(loaded.IsDamaged);
            Assert.Equal(ItemRarity.Rare, loaded.Rarity);
            Assert.Equal(Roll.Modifiers.Count, loaded.Modifiers.Count);
            for (int i = 0; i < Roll.Modifiers.Count; i++)
            {
                Assert.Equal(Roll.Modifiers[i].Stat, loaded.Modifiers[i].Stat);
                Assert.Equal(BitConverter.SingleToInt32Bits(Roll.Modifiers[i].Value), BitConverter.SingleToInt32Bits(loaded.Modifiers[i].Value));
            }
        }
    }

    [Fact]
    public void ASecondSaveWithADifferentRoll_LeavesTheStoredTextAlone()
    {
        using MigrationTestDatabase db = Migrated();
        (SqliteItemRepository items, SqliteInventoryRepository inventory) = Repositories(db);
        Give(items, inventory, Player, 0, Item(10, "gear.iron-blade", Roll));
        string before = (string)db.Scalar("SELECT rolled_modifiers FROM item_instances WHERE item_instance_id = 10;")!;

        ItemRoll other = new(ItemRarity.Common, [new StatModifier(StatId.Strength, StatOp.Flat, 1f)]);
        items.SaveItemInstance(Item(10, "gear.iron-blade", other) with { DisplayName = "Renamed" });
        items.SaveItemInstance(Item(10, "gear.iron-blade", roll: null));

        Assert.Equal(before, (string)db.Scalar("SELECT rolled_modifiers FROM item_instances WHERE item_instance_id = 10;")!);
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM item_instances WHERE display_name = 'Item';"));
    }

    [Fact]
    public void AnUnrolledRow_ReadsNull_AndAValidJsonBadRoll_ReadsDamagedAndLoads()
    {
        using MigrationTestDatabase db = Migrated();
        (SqliteItemRepository items, SqliteInventoryRepository inventory) = Repositories(db);
        Give(items, inventory, Player, 0, Item(10, "tool.torch"));
        Give(items, inventory, Player, 1, Item(11, "gear.iron-blade", Roll));
        db.ExecuteScript(
            "UPDATE item_instances SET rolled_modifiers = '{\"v\":1,\"r\":\"Rare\",\"m\":[[\"Agility\",\"Flat\",5]]}' WHERE item_instance_id = 11;");

        PersistedPlayerInventory loaded = inventory.LoadPlayerInventory(Player);

        Assert.Equal(2, loaded.Items.Count);
        Assert.Null(loaded.Items.Single(i => i.ItemInstanceId == 10).Roll);
        ItemRoll? damaged = loaded.Items.Single(i => i.ItemInstanceId == 11).Roll;
        Assert.NotNull(damaged);
        Assert.True(damaged.IsDamaged);
        Assert.Empty(damaged.Modifiers);
    }

    [Fact]
    public void DeleteInventorySlot_RemovesOnlyThatRow()
    {
        using MigrationTestDatabase db = Migrated();
        (SqliteItemRepository items, SqliteInventoryRepository inventory) = Repositories(db);
        Give(items, inventory, Player, 3, Item(10, "tool.torch"));
        inventory.SaveInventorySlot(new PersistedInventorySlot(Player, 101, 10, default));
        Give(items, inventory, "p2", 101, Item(11, "tool.torch"));

        inventory.DeleteInventorySlot(Player, 101);

        Assert.Equal([3], inventory.LoadPlayerInventory(Player).Slots.Select(s => s.SlotIndex).ToArray());
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_inventory_slots WHERE player_name = 'p2';"));
        Assert.Equal(2L, db.Scalar("SELECT COUNT(*) FROM item_instances;"));
    }

    [Fact]
    public void DestroyInventoryItem_RemovesAllOfThePlayersRowsAndTheItem()
    {
        using MigrationTestDatabase db = Migrated();
        (SqliteItemRepository items, SqliteInventoryRepository inventory) = Repositories(db);
        Give(items, inventory, Player, 3, Item(10, "gear.iron-blade", Roll));
        inventory.SaveInventorySlot(new PersistedInventorySlot(Player, 100, 10, default));
        Give(items, inventory, Player, 4, Item(11, "tool.torch"));

        inventory.DestroyInventoryItem(Player, 10);

        Assert.Equal([4], inventory.LoadPlayerInventory(Player).Slots.Select(s => s.SlotIndex).ToArray());
        Assert.Equal([11L], items.LoadItemInstances().Select(i => i.ItemInstanceId).ToArray());
    }

    [Fact]
    public void DestroyInventoryItem_KeepsAnotherPlayersRowAndTheItemWhenShared()
    {
        using MigrationTestDatabase db = Migrated();
        (SqliteItemRepository items, SqliteInventoryRepository inventory) = Repositories(db);
        Give(items, inventory, Player, 3, Item(10, "tool.torch"));
        inventory.SaveInventorySlot(new PersistedInventorySlot("p2", 5, 10, default));

        inventory.DestroyInventoryItem(Player, 10);

        Assert.Empty(inventory.LoadPlayerInventory(Player).Slots);
        PersistedInventorySlot kept = Assert.Single(inventory.LoadPlayerInventory("p2").Slots);
        Assert.Equal(5, kept.SlotIndex);
        Assert.Single(items.LoadItemInstances());
    }

    [Fact]
    public void ADestroyOfAnItemTheNameDoesNotHold_ChangesNothingElse()
    {
        using MigrationTestDatabase db = Migrated();
        (SqliteItemRepository items, SqliteInventoryRepository inventory) = Repositories(db);
        Give(items, inventory, "p2", 1, Item(10, "tool.torch"));

        inventory.DestroyInventoryItem(Player, 10);

        // p2 still references it, so the item row stays.
        Assert.Single(inventory.LoadPlayerInventory("p2").Slots);
        Assert.Single(items.LoadItemInstances());
    }

    [Fact]
    public void ARolledRow_SurvivesAnExportCopyOfItsWorld_WithEqualBits()
    {
        using MigrationTestDatabase db = Migrated();
        (SqliteItemRepository items, SqliteInventoryRepository inventory) = Repositories(db);
        Give(items, inventory, Player, 0, Item(10, "gear.iron-blade", Roll));
        SqliteWorldStore store = new(db.Schema);
        Dream.Server.Worlds.WorldRecord copy = new Dream.Server.Persistence.Worlds.SqliteWorldCatalog(db.Schema)
            .Copy(Dream.Simulation.World.WorldId.Default, "Copy");

        IReadOnlyList<PersistedItemInstance> copied = new SqliteItemRepository(store, copy.Descriptor.Id).LoadItemInstances();

        ItemRoll roll = Assert.Single(copied).Roll!;
        Assert.Equal(
            BitConverter.SingleToInt32Bits(Roll.Modifiers[1].Value),
            BitConverter.SingleToInt32Bits(roll.Modifiers[1].Value));
    }
}
