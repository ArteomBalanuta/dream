using System.Globalization;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// M2-B: migration 0011 adds enemy_instances.camp_key (TEXT, NULL). A row written before camps keeps every
// value and reads back with no camp; a camp key written afterwards round-trips.
public sealed class SpawnCampsMigrationTests
{
    private static SqliteSchemaMigrator UpTo(int version) =>
        new([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(version)]);

    private static MigrationTestDatabase V10WithEnemies()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpTo(10));
        database.ExecuteScript(
            "INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, " +
            "facing_x, facing_y, facing_z, current_hp, max_hp, strength, agility, vitality, energy, movement_speed, attack_speed, level) VALUES " +
            "(1, 21, 'Wolf', 'Wolf', 5.5, 1, 2.5, 0, 0, 1, 100, 100, 10, 10, 10, 0, 2.8, 0.95238096, 1), " +
            "(1, 23, 'DireWolf', 'Dire Wolf', 1, 1, 1, 0, 0, -1, 0, 200, 16, 10, 30, 0, 3.2, 1, 3);");
        return database;
    }

    private static string Dump(MigrationTestDatabase database)
    {
        using SqliteConnection c = database.Open();
        using SqliteCommand command = c.CreateCommand();
        command.CommandText = "SELECT object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, " +
            "current_hp, max_hp, strength, agility, vitality, energy, movement_speed, attack_speed, level, created_utc, updated_utc " +
            "FROM enemy_instances ORDER BY object_id;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            string[] cells = new string[reader.FieldCount];
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
            }

            rows.Add(string.Join("|", cells));
        }

        return string.Join(";", rows);
    }

    [Fact]
    public void AV10Database_MigratesInOneStep_WithABackup_AndEveryEnemyRowKeepsItsValues_WithNoCamp()
    {
        using MigrationTestDatabase database = V10WithEnemies();
        string before = Dump(database);
        Assert.NotEmpty(before);
        SqliteConnection.ClearAllPools();

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(new SchemaMigrationResult(10, 12, result.BackupPath), result);
        Assert.Contains("v0010-to-v0012", Assert.Single(database.Backups()), StringComparison.Ordinal);
        Assert.Equal(12L, database.Scalar("SELECT MAX(version) FROM schema_version;"));
        Assert.Equal("spawn_camps", database.Scalar("SELECT name FROM schema_version WHERE version = 11;"));
        Assert.Equal(before, Dump(database));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM enemy_instances WHERE camp_key IS NULL;"));
        Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));
        // The object id counter trigger survives (no table was rebuilt).
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'trg_enemy_instances_advance_object_id';"));
    }

    [Fact]
    public void TheColumn_IsNullableText_WithNoDefault_AndANewDatabaseHasIt()
    {
        using MigrationTestDatabase database = new();

        database.Schema.EnsureSchema();

        Assert.Equal("TEXT", database.Scalar("SELECT type FROM pragma_table_info('enemy_instances') WHERE name = 'camp_key';"));
        Assert.Equal(0L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('enemy_instances') WHERE name = 'camp_key';"));
        Assert.Null(database.Scalar("SELECT dflt_value FROM pragma_table_info('enemy_instances') WHERE name = 'camp_key';") is DBNull ? null : "x");
        Assert.Empty(database.Backups());
    }

    [Fact]
    public void ACampKey_RoundTripsThroughTheRepository_AndALegacyRowReadsBackWithNone()
    {
        using MigrationTestDatabase database = V10WithEnemies();
        SqliteConnection.ClearAllPools();
        database.Schema.EnsureSchema();
        SqliteWorldPersistenceRepository world = new(database.Schema);

        // Saving over a legacy row sets its camp; a new row with a camp keeps it; a row with none stays null.
        PersistedEnemyInstance legacy = Assert.Single(world.LoadActiveEnemies(), row => row.ObjectId == new ObjectId(21));
        Assert.Null(legacy.CampKey);
        world.SaveEnemyInstance(legacy with { CampKey = "north-east-wolves" });
        world.SaveEnemyInstance(legacy with { ObjectId = new ObjectId(40), CampKey = "south-west-wolves" });
        world.SaveEnemyInstance(legacy with { ObjectId = new ObjectId(41), CampKey = null });

        IReadOnlyList<PersistedEnemyInstance> rows = world.LoadActiveEnemies();
        Assert.Equal("north-east-wolves", rows.Single(row => row.ObjectId == new ObjectId(21)).CampKey);
        Assert.Equal("south-west-wolves", rows.Single(row => row.ObjectId == new ObjectId(40)).CampKey);
        Assert.Null(rows.Single(row => row.ObjectId == new ObjectId(41)).CampKey);
        Assert.Null(rows.Single(row => row.ObjectId == new ObjectId(23)).CampKey);
    }
}
