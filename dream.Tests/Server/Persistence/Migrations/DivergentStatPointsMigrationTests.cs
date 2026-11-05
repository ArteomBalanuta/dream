using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// The stats-sheet line shipped its own migration 0011 (character_stat_points: player_profiles.stat_points)
// before the multiplayer line's 0011..0014 were merged. A database that ran it is at version 11 with the
// wrong 11; the migrator drops the column, forgets that row and runs 0011..0014 from 10, in the same
// transaction and after the backup. The result is the schema of a fresh database.
public sealed class DivergentStatPointsMigrationTests
{
    // Master's retired 0011_character_stat_points.sql, statements only, as it shipped.
    private const string RetiredMigration0011 = """
        ALTER TABLE player_profiles ADD COLUMN stat_points INTEGER NOT NULL DEFAULT 0 CHECK (stat_points >= 0);
        DROP TRIGGER IF EXISTS trg_player_profiles_set_updated_utc;
        UPDATE player_profiles SET stat_points = (level - 1) * 5;
        CREATE TRIGGER IF NOT EXISTS trg_player_profiles_set_updated_utc
        AFTER UPDATE ON player_profiles
        FOR EACH ROW
        WHEN NEW.updated_utc = OLD.updated_utc
        BEGIN
            UPDATE player_profiles
            SET updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE player_name = NEW.player_name;
        END;
        """;

    private static SqliteSchemaMigrator UpTo(int version) =>
        new([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(version)]);

    private static MigrationTestDatabase DivergentV11()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpTo(10));
        database.ExecuteScript("INSERT INTO player_profiles (player_name, display_name, level, strength) VALUES ('hero', 'Hero', 4, 14);");
        database.ExecuteScript(RetiredMigration0011);
        database.ExecuteScript("INSERT INTO schema_version (version, name, applied_utc) VALUES (11, 'character_stat_points', '2026-11-01T00:00:00Z');");
        return database;
    }

    [Fact]
    public void ADivergentV11Database_IsRepaired_AndMigratesTo14_WithTheFreshSchema()
    {
        using MigrationTestDatabase database = DivergentV11();
        Assert.Equal(15L, database.Scalar("SELECT stat_points FROM player_profiles WHERE player_name = 'hero';"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('enemy_instances') WHERE name = 'camp_key';"));
        SqliteConnection.ClearAllPools();
        List<string> log = [];

        SchemaMigrationResult result = database.Schema.EnsureSchema(log.Add);

        Assert.Equal(new SchemaMigrationResult(11, 15, result.BackupPath), result);
        string backup = Assert.Single(database.Backups());
        Assert.Contains("v0011-to-v0015", backup, StringComparison.Ordinal);
        Assert.Contains(log, line => line.Contains("character_stat_points", StringComparison.Ordinal));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('enemy_instances') WHERE name = 'camp_key';"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('player_profiles') WHERE name = 'stat_points';"));
        Assert.Equal(15L, database.Scalar("SELECT MAX(version) FROM schema_version;"));
        Assert.Equal("spawn_camps", database.Scalar("SELECT name FROM schema_version WHERE version = 11;"));
        Assert.Equal(15L, database.Scalar("SELECT COUNT(*) FROM schema_version;"));
        // The profile row kept its values; the points are derived from the level now.
        Assert.Equal(4L, database.Scalar("SELECT level FROM player_profiles WHERE player_name = 'hero';"));
        Assert.Equal(14L, database.Scalar("SELECT strength FROM player_profiles WHERE player_name = 'hero';"));
        Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));

        using MigrationTestDatabase fresh = new();
        fresh.Schema.EnsureSchema();
        SqliteConnection.ClearAllPools();
        using (SqliteConnection expected = fresh.Open())
        using (SqliteConnection actual = database.Open())
        {
            Assert.Equal(SqliteSchemaSnapshot.Capture(expected), SqliteSchemaSnapshot.Capture(actual));
        }

        // The backup is the untouched divergent file.
        using SqliteConnection old = MigrationTestDatabase.OpenFile(backup);
        using SqliteCommand command = old.CreateCommand();
        command.CommandText = "SELECT name FROM schema_version WHERE version = 11;";
        Assert.Equal("character_stat_points", command.ExecuteScalar());
    }

    [Fact]
    public void AnMpV11Database_IsNotTouchedByTheRepair()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpTo(11));
        SqliteConnection.ClearAllPools();

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(new SchemaMigrationResult(11, 15, result.BackupPath), result);
        Assert.Equal("spawn_camps", database.Scalar("SELECT name FROM schema_version WHERE version = 11;"));
        Assert.Equal(15L, database.Scalar("SELECT COUNT(*) FROM schema_version;"));
    }
}
