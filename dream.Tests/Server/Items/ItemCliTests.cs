using Dream.Server.Items;
using Dream.Server.Persistence.Migrations;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Items;

// Wave H2: --items grant | list.
public sealed class ItemCliTests
{
    private static MigrationTestDatabase Migrated(bool withGrant = true)
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqliteConnection.ClearAllPools();
        db.ExecuteScript("UPDATE worlds SET seed = '12345', world_time_ticks = 900 WHERE world_id = 1;");
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('bob~2', 'Bob');");
        if (withGrant)
        {
            db.ExecuteScript("INSERT INTO player_starter_grants (world_id, player_name, kit_version, updated_utc) VALUES (1, 'bob~2', 2, '2026-01-01T00:00:00.0000000Z');");
        }

        return db;
    }

    private static (int Exit, string Out, string Err) Run(
        MigrationTestDatabase db,
        Func<WorldId, IDisposable?>? lease,
        params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = ItemCli.Run(args, () => db.Schema, output, error, lease);
        return (exit, output.ToString(), error.ToString());
    }

    private static (int Exit, string Out, string Err) Run(MigrationTestDatabase db, params string[] args) =>
        Run(db, static _ => new Lease(), args);

    private sealed class Lease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static PersistedPlayerInventory Load(MigrationTestDatabase db) =>
        new SqliteInventoryRepository(new SqliteWorldStore(db.Schema)).LoadPlayerInventory("bob~2");

    [Fact]
    public void Grant_RollsIntoTheLowestFreeSlot_AndTheIdIsBelowTheRaisedCounter()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript(
            "INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, owner_player_name, state, stack_count) " +
            "VALUES (1, 50, 'tool.torch', 'T', 'v', 'bob~2', 'Inventory', 1);" +
            "INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id) VALUES (1, 'bob~2', 0, 50), (1, 'bob~2', 2, 50);");

        (int exit, string output, string error) = Run(db, "grant", "1", "bob", "gear.iron-blade", "--rarity", "rare");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
        string[] fields = output.TrimEnd().Split('\t');
        Assert.Equal(4, fields.Length);
        long id = long.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("1", fields[1]);
        Assert.Equal("rare", fields[2]);
        Assert.Contains("Strength=", fields[3], StringComparison.Ordinal);
        Assert.True(id > 50);
        Assert.True(id < (long)db.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;")!);

        PersistedPlayerInventory loaded = Load(db);
        PersistedItemInstance item = loaded.Items.Single(i => i.ItemInstanceId == id);
        Assert.Equal("gear.iron-blade", item.ItemKind);
        Assert.Equal("Inventory", item.State);
        Assert.Equal("bob~2", item.OwnerPlayerName);
        Assert.NotNull(item.Roll);
        Assert.Equal(ItemRarity.Rare, item.Roll.Rarity);
        Assert.Equal(id, loaded.Slots.Single(s => s.SlotIndex == 1).ItemInstanceId);
        // The starter grant row is untouched: no top-up side effect.
        Assert.Equal(2, loaded.StarterKitVersion);
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_starter_grants;"));
    }

    [Fact]
    public void Grant_DefaultsToCommon_AndTwoGrantsGetDifferentIds_AndSlots()
    {
        using MigrationTestDatabase db = Migrated();

        (int a, string first, _) = Run(db, "grant", "1", "Bob", "gear.leather-cap");
        (int b, string second, _) = Run(db, "grant", "1", "bob~2", "gear.leather-cap");

        Assert.Equal(0, a);
        Assert.Equal(0, b);
        string[] x = first.TrimEnd().Split('\t');
        string[] y = second.TrimEnd().Split('\t');
        Assert.Equal("common", x[2]);
        Assert.NotEqual(x[0], y[0]);
        Assert.Equal(["0", "1"], new[] { x[1], y[1] });
    }

    [Fact]
    public void Grant_IsDeterministicForTheSameDatabaseState()
    {
        string Granted()
        {
            using MigrationTestDatabase db = Migrated();
            return Run(db, "grant", "1", "Bob", "gear.iron-blade", "--rarity", "uncommon").Out;
        }

        Assert.Equal(Granted(), Granted());
    }

    [Fact]
    public void Grant_RefusesAHeldWorld_WithExit7_AndChangesNothing()
    {
        using MigrationTestDatabase db = Migrated();

        (int exit, _, string error) = Run(db, static _ => null, "grant", "1", "Bob", "gear.iron-blade");

        Assert.Equal(7, exit);
        Assert.Contains("in use", error, StringComparison.Ordinal);
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM item_instances;"));
    }

    [Fact]
    public void Grant_RefusesACharacterWithNoGrantRow_AFullBackpack_AndAKindWithoutRolls()
    {
        using MigrationTestDatabase noGrant = Migrated(withGrant: false);
        (int exitNoGrant, _, string errorNoGrant) = Run(noGrant, "grant", "1", "Bob", "gear.iron-blade");
        Assert.Equal(2, exitNoGrant);
        Assert.Contains("starter-grant", errorNoGrant, StringComparison.Ordinal);
        Assert.Equal(0L, noGrant.Scalar("SELECT COUNT(*) FROM item_instances;"));

        using MigrationTestDatabase full = Migrated();
        full.ExecuteScript(
            "INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, owner_player_name, state, stack_count) " +
            "VALUES (1, 50, 'tool.torch', 'T', 'v', 'bob~2', 'Inventory', 1);");
        for (int slot = 0; slot < InventoryConstants.BackpackSlotCount; slot++)
        {
            full.ExecuteScript($"INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id) VALUES (1, 'bob~2', {slot}, 50);");
        }

        (int exitFull, _, string errorFull) = Run(full, "grant", "1", "Bob", "gear.iron-blade");
        Assert.Equal(2, exitFull);
        Assert.Contains("full", errorFull, StringComparison.Ordinal);
        Assert.Equal(1L, full.Scalar("SELECT COUNT(*) FROM item_instances;"));

        using MigrationTestDatabase plain = Migrated();
        Assert.Equal(2, Run(plain, "grant", "1", "Bob", "tool.torch").Exit);
        Assert.Equal(2, Run(plain, "grant", "1", "Bob", "no.such.kind").Exit);
        Assert.Equal(0L, plain.Scalar("SELECT COUNT(*) FROM item_instances;"));
    }

    [Fact]
    public void Grant_UnknownWorldOrCharacter_Exits2()
    {
        using MigrationTestDatabase db = Migrated();

        Assert.Equal(2, Run(db, "grant", "99", "Bob", "gear.iron-blade").Exit);
        Assert.Equal(2, Run(db, "grant", "nowhere", "Bob", "gear.iron-blade").Exit);
        Assert.Equal(2, Run(db, "grant", "1", "Nobody", "gear.iron-blade").Exit);
        Assert.Equal(2, Run(db, "list", "1", "Nobody").Exit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("grant 1 Bob")]
    [InlineData("grant 1 Bob gear.iron-blade --rarity")]
    [InlineData("grant 1 Bob gear.iron-blade --rarity epic")]
    [InlineData("grant 1 Bob gear.iron-blade --rarity rare --rarity rare")]
    [InlineData("list 1")]
    [InlineData("list 1 Bob --rarity rare")]
    [InlineData("burn 1 Bob")]
    [InlineData("grant 0 Bob gear.iron-blade")]
    public void BadArguments_ExitWithUsage(string line)
    {
        using MigrationTestDatabase db = Migrated();

        (int exit, _, string error) = Run(db, line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(1, exit);
        Assert.Contains("Usage", error, StringComparison.Ordinal);
    }

    [Fact]
    public void List_PrintsSlotsKindsRarityAndModifiers_AndTakesNoLease()
    {
        using MigrationTestDatabase db = Migrated();
        Run(db, "grant", "1", "Bob", "gear.ward-lantern", "--rarity", "uncommon");
        db.ExecuteScript(
            "INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, owner_player_name, state, stack_count) " +
            "VALUES (1, 500, 'tool.torch', 'T', 'v', 'bob~2', 'Inventory', 1);" +
            "INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id) VALUES (1, 'bob~2', 5, 500), (1, 'bob~2', 100, 500);");

        (int exit, string output, _) = Run(db, static _ => null, "list", "1", "bob");

        Assert.Equal(0, exit);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        string[] rolled = lines[0].Split('\t');
        Assert.Equal("0", rolled[0]);
        Assert.Equal("gear.ward-lantern", rolled[2]);
        Assert.Equal("uncommon", rolled[3]);
        Assert.StartsWith("Armor=", rolled[4], StringComparison.Ordinal);
        Assert.Equal(["5", "500", "tool.torch", "-", "-"], lines[1].Split('\t'));
        Assert.Equal("100", lines[2].Split('\t')[0]);
    }

    [Fact]
    public void ASchemaNewerThanTheBuild_Exits3_AndInvalidContentExits8()
    {
        using MigrationTestDatabase db = Migrated();
        StringWriter output = new();
        StringWriter error = new();

        int tooNew = ItemCli.Run(
            ["list", "1", "Bob"], static () => throw new SchemaTooNewException(99, 8), output, error);
        int badContent = ItemCli.Run(
            ["grant", "1", "Bob", "gear.iron-blade"], () => db.Schema, output, error,
            static _ => new Lease(), static () => throw new ContentValidationException("broken"));

        Assert.Equal(3, tooNew);
        Assert.Equal(8, badContent);
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM item_instances;"));
    }

    [Fact]
    public void ControlCharactersInAKindName_NeverReachTheOutput()
    {
        using MigrationTestDatabase db = Migrated();

        (int exit, _, string error) = Run(db, "grant", "1", "Bob", "bad\tkind\nx");

        Assert.Equal(2, exit);
        Assert.DoesNotContain('\t', error);
        Assert.DoesNotContain("kind\nx", error, StringComparison.Ordinal);
    }
}
