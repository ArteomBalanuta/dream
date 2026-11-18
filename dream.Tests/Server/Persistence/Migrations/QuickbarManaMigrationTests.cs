using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// M4: migration 0013 sets every stored current_energy high (the join clamps it to the character's
// maximum) and adds player_quickbar_slots, which cascades with its world and its profile and refuses a
// slot or kind out of range.
public sealed class QuickbarManaMigrationTests
{
    private static MigrationTestDatabase V12WithPlayers()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(new SqliteSchemaMigrator([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(12)]));
        database.ExecuteScript(
            "INSERT INTO player_profiles (player_name, display_name) VALUES ('p1', 'P1'), ('p2', 'P2');" +
            "INSERT INTO player_world_state (world_id, player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, current_energy) " +
            "VALUES (1, 'p1', 0, 0, 0, 0, 0, 1, 80, 0), (1, 'p2', 1, 0, 1, 0, 0, 1, 90, 10);");
        return database;
    }

    private static void ExecuteWithForeignKeys(MigrationTestDatabase database, string sql)
    {
        using SqliteConnection connection = database.Open();
        MigrationTestDatabase.Execute(connection, "PRAGMA foreign_keys = ON;");
        MigrationTestDatabase.Execute(connection, sql);
    }

    private static string Row(string player, int slot, int kind, string key) =>
        $"INSERT INTO player_quickbar_slots (world_id, player_name, slot, kind, ref_key, updated_utc) VALUES (1, '{player}', {slot}, {kind}, '{key}', '2026-01-01T00:00:00.000Z');";

    [Fact]
    public void EveryStoredEnergy_IsRaised_AndTheRestOfTheRowIsKept()
    {
        using MigrationTestDatabase database = V12WithPlayers();
        SqlitePools.Release(database.DatabasePath);

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(17, result.ToVersion);
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM player_world_state WHERE current_energy = 1000000;"));
        Assert.Equal(80L, database.Scalar("SELECT current_hp FROM player_world_state WHERE player_name = 'p1';"));
        Assert.Equal(90L, database.Scalar("SELECT current_hp FROM player_world_state WHERE player_name = 'p2';"));
    }

    [Fact]
    public void TheQuickbarTable_StartsEmpty_AndTakesRows()
    {
        using MigrationTestDatabase database = V12WithPlayers();
        database.Schema.EnsureSchema();
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM player_quickbar_slots;"));

        ExecuteWithForeignKeys(database, Row("p1", 0, 1, "skill.cleave") + Row("p1", 5, 0, string.Empty) + Row("p2", 0, 2, "potion.health"));

        Assert.Equal(3L, database.Scalar("SELECT COUNT(*) FROM player_quickbar_slots;"));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(6, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 3)]
    public void ASlotOrKindOutOfRange_IsRefused(int slot, int kind)
    {
        using MigrationTestDatabase database = V12WithPlayers();
        database.Schema.EnsureSchema();

        Assert.Throws<SqliteException>(() => ExecuteWithForeignKeys(database, Row("p1", slot, kind, "x")));
    }

    [Fact]
    public void ARowForAnUnknownPlayerOrWorld_IsRefused_AndASlotIsUniquePerPlayer()
    {
        using MigrationTestDatabase database = V12WithPlayers();
        database.Schema.EnsureSchema();

        Assert.Throws<SqliteException>(() => ExecuteWithForeignKeys(database, Row("ghost", 0, 1, "skill.cleave")));
        Assert.Throws<SqliteException>(() => ExecuteWithForeignKeys(
            database,
            "INSERT INTO player_quickbar_slots (world_id, player_name, slot, kind, ref_key, updated_utc) VALUES (99, 'p1', 0, 1, 'x', 'u');"));
        ExecuteWithForeignKeys(database, Row("p1", 2, 1, "skill.bolt"));
        Assert.Throws<SqliteException>(() => ExecuteWithForeignKeys(database, Row("p1", 2, 1, "skill.mend")));
    }

    [Fact]
    public void AWorldCopy_CarriesTheQuickbar_AndDeletingTheCopyKeepsTheSource()
    {
        using Dream.Tests.Server.Worlds.WorldCatalogTestDatabase db = new();
        db.SeedWorldOne();
        db.Execute(
            "INSERT INTO player_quickbar_slots (world_id, player_name, slot, kind, ref_key, updated_utc) VALUES " +
            "(1, 'alice', 0, 1, 'skill.cleave', '2026-01-01T00:00:00Z'), (1, 'alice', 4, 2, 'potion.health', '2026-01-01T00:00:00Z');");

        Dream.Simulation.World.WorldId copyId = db.Catalog.Copy(Dream.Simulation.World.WorldId.Default, "Copy").Descriptor.Id;

        Assert.Equal(2L, db.Count("player_quickbar_slots", copyId.Value));
        Assert.Equal("potion.health", db.Scalar($"SELECT ref_key FROM player_quickbar_slots WHERE world_id = {copyId.Value} AND slot = 4;"));
        db.Catalog.Delete(copyId);
        Assert.Equal(0L, db.Count("player_quickbar_slots", copyId.Value));
        Assert.Equal(2L, db.Count("player_quickbar_slots", 1));
    }

    [Fact]
    public void DeletingAProfile_TakesItsQuickbarWithIt()
    {
        using MigrationTestDatabase database = V12WithPlayers();
        database.Schema.EnsureSchema();
        ExecuteWithForeignKeys(database, Row("p1", 0, 1, "skill.cleave") + Row("p2", 0, 1, "skill.bolt"));

        ExecuteWithForeignKeys(database, "DELETE FROM player_profiles WHERE player_name = 'p1';");

        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM player_quickbar_slots WHERE player_name = 'p1';"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM player_quickbar_slots WHERE player_name = 'p2';"));
    }
}
