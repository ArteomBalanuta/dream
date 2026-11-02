using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// M3: migration 0012 removes the extra slot rows of an item that more than one row references, then adds
// the unique index (world_id, item_instance_id), the ground-item index and the wallets table. The keeper
// of a shared item is deterministic: the owner's own row, then a worn row over a bag row over a held-back
// row, then the lowest player name and slot. The other rows go; the item never does.
public sealed class SharedSlotCleanupMigrationTests
{
    private static MigrationTestDatabase V11()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(new SqliteSchemaMigrator([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(11)]));
        database.ExecuteScript(
            "INSERT INTO player_profiles (player_name, display_name) VALUES ('p1', 'P1'), ('p2', 'P2');");
        return database;
    }

    private static string Item(long id, string? owner) =>
        "INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, owner_player_name, state, stack_count) " +
        $"VALUES (1, {id}, 'tool.torch', 'T', 'v', {(owner == null ? "NULL" : $"'{owner}'")}, 'Inventory', 1);";

    private static string Slot(string player, int slot, long item) =>
        $"INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id) VALUES (1, '{player}', {slot}, {item});";

    private static List<string> Rows(MigrationTestDatabase database)
    {
        using SqliteConnection connection = database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT player_name || '|' || slot_index || '|' || item_instance_id FROM player_inventory_slots ORDER BY item_instance_id, player_name, slot_index;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    [Fact]
    public void TheOwnersRow_BeatsAnotherPlayersWornRow()
    {
        using MigrationTestDatabase database = V11();
        database.ExecuteScript(Item(1, "p1") + Slot("p2", 100, 1) + Slot("p1", 5, 1));

        database.Schema.EnsureSchema();

        Assert.Equal(["p1|5|1"], Rows(database));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM item_instances WHERE item_instance_id = 1;"));
    }

    [Fact]
    public void ForOneOwner_WornBeatsBag_AndBagBeatsHeldBack()
    {
        using MigrationTestDatabase database = V11();
        database.ExecuteScript(
            Item(1, "p1") + Slot("p1", 3, 1) + Slot("p1", 100, 1) +
            Item(2, "p1") + Slot("p1", 200, 2) + Slot("p1", 9, 2) +
            Item(3, "p1") + Slot("p1", 215, 3) + Slot("p1", 101, 3) + Slot("p1", 4, 3));

        database.Schema.EnsureSchema();

        Assert.Equal(["p1|100|1", "p1|9|2", "p1|101|3"], Rows(database));
    }

    [Fact]
    public void WhenNoRowIsTheOwners_TheKindOfSlotDecides_ThenTheLowestPlayerAndSlot()
    {
        using MigrationTestDatabase database = V11();
        database.ExecuteScript(
            // No owner: a worn row of p2 beats a bag row of p1.
            Item(1, null) + Slot("p1", 4, 1) + Slot("p2", 100, 1) +
            // Same kind of slot: the lowest player name wins.
            Item(2, null) + Slot("p2", 7, 2) + Slot("p1", 8, 2) +
            // Same player and kind: the lowest slot wins.
            Item(3, null) + Slot("p1", 12, 3) + Slot("p1", 6, 3) +
            // An owner that holds none of the rows: the same order.
            Item(4, "p2") + Slot("p1", 30, 4) + Slot("p1", 31, 4));

        database.Schema.EnsureSchema();

        Assert.Equal(["p2|100|1", "p1|8|2", "p1|6|3", "p1|30|4"], Rows(database));
    }

    [Fact]
    public void ItemsWithOneRow_AreUntouched_AndTheIndexAndTheWalletTableExist()
    {
        using MigrationTestDatabase database = V11();
        database.ExecuteScript(Item(1, "p1") + Slot("p1", 0, 1) + Item(2, "p2") + Slot("p2", 0, 2) + Slot("p2", 100, 1));

        database.Schema.EnsureSchema();

        Assert.Equal(["p1|0|1", "p2|0|2"], Rows(database));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_player_inventory_slots_item' AND sql LIKE 'CREATE UNIQUE INDEX%';"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_player_inventory_slots_item_instance_id';"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_item_instances_ground';"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM player_wallets;"));
        Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));
        Assert.Throws<SqliteException>(() => database.ExecuteScript(Slot("p1", 9, 1)));
    }

    [Fact]
    public void TheWalletTable_RefusesNegativeGold_AndIsClearedWithItsPlayer()
    {
        using MigrationTestDatabase database = V11();
        database.Schema.EnsureSchema();
        database.ExecuteScript("INSERT INTO player_wallets (world_id, player_name, gold, updated_utc) VALUES (1, 'p1', 5, '2026-01-01T00:00:00.000Z');");

        Assert.Throws<SqliteException>(() => database.ExecuteScript(
            "UPDATE player_wallets SET gold = -1 WHERE player_name = 'p1';"));

        database.ExecuteScript("PRAGMA foreign_keys = ON; DELETE FROM player_profiles WHERE player_name = 'p1';");
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM player_wallets;"));
    }
}
