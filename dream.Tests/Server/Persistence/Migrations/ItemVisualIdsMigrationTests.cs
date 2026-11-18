using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Migration 0016 sets item_instances.visual_id from the content catalog for every known item_kind, so a
// character saved before the worn-equipment-visuals wave (flat ids such as 'worn.head') gets the per-item
// id ArmourMeshCatalog resolves. A kind the catalog does not know is left exactly as stored, and a second
// run changes nothing.
public sealed class ItemVisualIdsMigrationTests
{
    private static MigrationTestDatabase V15WithItems()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(new SqliteSchemaMigrator([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(15)]));
        database.ExecuteScript(
            "INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, state) VALUES " +
            "(1, 9001, 'gear.leather-cap', 'Leather Cap', 'worn.head', 'Bag')," +
            "(1, 9002, 'gear.long-sword', 'Long Sword', 'held.weapon.sword', 'Bag')," +
            "(1, 9003, 'gear.no-such-thing', 'Mystery', 'worn.legacy-flat-id', 'Bag');");
        SqlitePools.Release(database.DatabasePath);
        return database;
    }

    private static string Visual(MigrationTestDatabase database, long id) =>
        (string)database.Scalar($"SELECT visual_id FROM item_instances WHERE world_id = 1 AND item_instance_id = {id};")!;

    private static string AllVisuals(MigrationTestDatabase database) =>
        string.Join("|", new[] { 9001L, 9002L, 9003L }.Select(id => Visual(database, id)));

    [Fact]
    public void AFlatWornId_BecomesThePerItemId()
    {
        using MigrationTestDatabase database = V15WithItems();

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(17, result.ToVersion);
        Assert.Equal("worn.head.leather-cap", Visual(database, 9001));
        Assert.Equal("held.weapon.sword", Visual(database, 9002));
    }

    [Fact]
    public void AKindNotInTheCatalog_IsLeftExactlyAsStored()
    {
        using MigrationTestDatabase database = V15WithItems();

        database.Schema.EnsureSchema();

        Assert.Equal("worn.legacy-flat-id", Visual(database, 9003));
    }

    [Fact]
    public void ASecondRun_ChangesNothing()
    {
        using MigrationTestDatabase database = V15WithItems();
        database.Schema.EnsureSchema();
        string after = AllVisuals(database);

        database.ExecuteScript(File.ReadAllText(Path.Combine(MigrationTestDatabase.MigrationsDirectory, "0016_item_visual_ids.sql")));
        SqlitePools.Release(database.DatabasePath);

        Assert.Equal(after, AllVisuals(database));
    }
}
