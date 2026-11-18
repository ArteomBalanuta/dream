using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Persistence wave A (A1): databases written by pre-migration servers (no schema_version) migrate to
// version 1 without losing or changing a row, and end with today's schema.
public sealed class LegacySchemaMigrationTests
{
    private static readonly string[] WaveINewFormulaKeys = ["enemy.attack_damage", "enemy.max_health", "enemy.attack_interval_seconds", "player.damage_taken_factor"];

    private const string ProfileAndChildRows = """
        INSERT INTO player_world_state VALUES ('alice', 1.5, 2.25, -3.125, 0.0, 0.0, 1.0, 80, 9, '2026-01-02T03:04:07.0000000Z');
        INSERT INTO cube_fracture_pieces (source_cube_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z,
            center_x, center_y, center_z, side_length, material_id)
            VALUES (8, 0, 'alice', 1.0, 2.0, 3.0, 1.25, 2.25, 3.25, 0.5, 'Stone'), (8, 1, 'alice', 1.0, 2.0, 3.0, 1.75, 2.25, 3.25, 0.5, 'Stone');
        INSERT INTO enemy_instances (enemy_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z,
            current_hp, max_hp, level) VALUES (3, 'Goblin', 'Goblin', 4.0, 5.0, 6.0, 1.0, 0.0, 0.0, 10, 20, 2);
        INSERT INTO item_instances (item_instance_id, item_kind, display_name, visual_id, owner_player_name, world_position_x,
            world_position_y, world_position_z, state, stack_count)
            VALUES (11, 'Sword', 'Sword', 'sword_01', 'alice', NULL, NULL, NULL, 'Inventory', 1),
                   (12, 'Potion', 'Potion', 'potion_01', NULL, 7.0, 8.0, 9.0, 'World', 3);
        INSERT INTO player_inventory_slots (player_name, slot_index, item_instance_id, updated_utc)
            VALUES ('alice', 0, 11, '2026-01-02T03:04:08.000Z');
        INSERT INTO player_profile_change_log (player_name, change_kind, previous_strength, new_strength, changed_utc)
            VALUES ('alice', 'StatChange', 10, 12, '2026-01-02T03:04:09.000Z');
        UPDATE combat_formulas SET expression_text = '7 + strength * 2' WHERE formula_key = 'player.min_damage';
        """;

    [Fact]
    public void PreMigrationDatabaseWithRowsInEveryTable_MigratesToVersion1_RowsAndSchemaUnchanged_WithABackup()
    {
        using MigrationTestDatabase reference = new();
        reference.BuildWithPreMigrationBootstrapper();
        using MigrationTestDatabase database = new();
        database.BuildWithPreMigrationBootstrapper();
        database.ExecuteScript("""
            INSERT INTO player_profiles (player_name, display_name, interaction_mode, strength, agility, vitality, energy,
                movement_speed, attack_speed, level, experience, current_hp, max_hp, created_utc, updated_utc)
                VALUES ('alice', 'Alice', 'Combat', 12, 11, 13, 9, 6.5, 2.25, 4, 1234, 80, 120,
                        '2026-01-02T03:04:05.1234567Z', '2026-01-02T03:04:06.123Z');
            INSERT INTO placed_cube_instances (cube_id, owner_player_name, cell_x, cell_y, cell_z, center_x, center_y, center_z,
                shape_kind, material_id, current_health, max_health)
                VALUES (7, 'alice', 1.0, 2.0, 3.0, 1.5, 2.5, 3.5, 'SolidCube', 'Stone', 4, 5),
                       (9, 'alice', 4.0, 2.0, 3.0, NULL, NULL, NULL, 'SolidCube', 'Wood', 5, 5);
            INSERT INTO world_metadata (key, value) VALUES ('simulation_seed', '12345'), ('world_time_ticks', '777');
            """ + ProfileAndChildRows);
        List<string> log = [];
        IReadOnlyList<string> rowsBefore;
        using (SqliteConnection connection = database.Open())
        {
            rowsBefore = SqliteSchemaSnapshot.DumpRows(connection);
        }

        // The baseline alone, which must reproduce today's schema; later migrations add to it.
        SchemaMigrationResult result = database.Schema.EnsureSchema(MigrationTestDatabase.BaselineOnlyMigrator(), log.Add);

        Assert.Equal(0, result.FromVersion);
        Assert.Equal(1, result.ToVersion);
        string backup = Assert.Single(database.Backups());
        Assert.Equal(backup, result.BackupPath);
        Assert.Matches(@"persistence\.sqlite\.v0000-to-v0001\.\d{8}T\d{6}Z\.bak$", backup);
        Assert.Contains(log, line => line.Contains(backup, StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("from version 0 to 1", StringComparison.Ordinal));
        using (SqliteConnection connection = database.Open())
        using (SqliteConnection expected = reference.Open())
        {
            Assert.Equal(rowsBefore, SqliteSchemaSnapshot.DumpRows(connection));
            Assert.Equal(SqliteSchemaSnapshot.Capture(expected), SqliteSchemaSnapshot.Capture(connection));
            Assert.Equal([(1L, "baseline")], SchemaMigrationTests.VersionRows(connection));
            Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode;"));
        }

        // The backup is the database as it was: every row, and no schema_version.
        using SqliteConnection backupConnection = MigrationTestDatabase.OpenFile(backup);
        Assert.Equal(rowsBefore, SqliteSchemaSnapshot.DumpRows(backupConnection));
        Assert.Equal(0L, Scalar(backupConnection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'schema_version';"));
    }

    [Fact]
    public void DatabaseFromBeforeTheCenterAndExperienceColumns_GainsThem_KeepsItsRowsAndSeed()
    {
        using MigrationTestDatabase reference = new();
        reference.BuildWithPreMigrationBootstrapper();
        using MigrationTestDatabase database = new();
        database.ExecuteScript(File.ReadAllText(MigrationTestDatabase.PreCenterExperienceFixturePath));
        database.ExecuteScript("""
            INSERT INTO player_profiles (player_name, display_name, interaction_mode, strength, agility, vitality, energy,
                movement_speed, attack_speed, level, current_hp, max_hp, created_utc, updated_utc)
                VALUES ('alice', 'Alice', 'Combat', 12, 11, 13, 9, 6.5, 2.25, 4, 80, 120,
                        '2026-01-02T03:04:05.1234567Z', '2026-01-02T03:04:06.123Z');
            INSERT INTO placed_cube_instances (cube_id, owner_player_name, cell_x, cell_y, cell_z, shape_kind, material_id,
                current_health, max_health) VALUES (7, 'alice', 1.0, 2.0, 3.0, 'SolidCube', 'Stone', 4, 5);
            """ + ProfileAndChildRows);
        Assert.Equal("delete", database.Scalar("PRAGMA journal_mode;"));
        IReadOnlyList<string> rowsBefore;
        using (SqliteConnection connection = database.Open())
        {
            rowsBefore = SqliteSchemaSnapshot.DumpRows(connection);
        }

        SchemaMigrationResult result = database.Schema.EnsureSchema(MigrationTestDatabase.BaselineOnlyMigrator());

        Assert.Equal(new SchemaMigrationResult(0, 1, result.BackupPath), result);
        Assert.NotNull(result.BackupPath);
        using SqliteConnection migrated = database.Open();
        using SqliteConnection expected = reference.Open();
        Assert.Equal(
            SqliteSchemaSnapshot.Capture(expected, ignoreColumnOrder: true),
            SqliteSchemaSnapshot.Capture(migrated, ignoreColumnOrder: true));
        // ...but not in column order: the snapshot does see where ALTER TABLE put them.
        Assert.NotEqual(SqliteSchemaSnapshot.Capture(expected), SqliteSchemaSnapshot.Capture(migrated));
        // The added columns are appended, so each old row reads as before plus their defaults.
        List<string> expectedRows = [.. rowsBefore.Select(row =>
            row.StartsWith("player_profiles ", StringComparison.Ordinal) ? row + " experience=Int64:0"
            : row.StartsWith("placed_cube_instances ", StringComparison.Ordinal) ? row + " center_x=NULL center_y=NULL center_z=NULL"
            : row)];
        Assert.Equal(expectedRows, SqliteSchemaSnapshot.DumpRows(migrated));
        // The seed keeps the edited formula: the baseline's insert only adds missing keys.
        Assert.Equal("7 + strength * 2", Scalar(migrated, "SELECT expression_text FROM combat_formulas WHERE formula_key = 'player.min_damage';"));
        Assert.Equal(8L, Scalar(migrated, "SELECT COUNT(*) FROM combat_formulas;"));
        Assert.Equal(0L, Scalar(migrated, "SELECT COUNT(*) FROM world_metadata;"));
        Assert.Equal("wal", Scalar(migrated, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void PreMigrationDatabase_TakesEveryShippedMigrationInOneStep_WithOneBackup()
    {
        using MigrationTestDatabase database = new();
        database.BuildWithPreMigrationBootstrapper();
        database.ExecuteScript("""
            INSERT INTO player_profiles (player_name, display_name) VALUES ('alice', 'Alice');
            """ + ProfileAndChildRows);
        IReadOnlyList<string> rowsBefore;
        using (SqliteConnection connection = database.Open())
        {
            rowsBefore = SqliteSchemaSnapshot.DumpRows(connection);
        }

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(new SchemaMigrationResult(0, MigrationTestDatabase.ShippedLatestVersion, result.BackupPath), result);
        string backup = Assert.Single(database.Backups());
        Assert.Matches(@"persistence\.sqlite\.v0000-to-v0017\.\d{8}T\d{6}Z\.bak$", backup);
        using SqliteConnection migrated = database.Open();
        // Migration 0003 moves the per-world tables to (world_id, ...) keys and the seed and clock onto
        // the worlds row, so only the tables it leaves alone compare equal here; the per-world rows are
        // checked field by field in WorldsMigrationTests.
        IReadOnlyList<string> rowsAfter = SqliteSchemaSnapshot.DumpRows(migrated);
        string[] unchangedTables = ["player_profiles ", "player_profile_change_log ", "combat_formulas "];
        Assert.Equal(
            rowsBefore
                .Where(row => unchangedTables.Any(table => row.StartsWith(table, StringComparison.Ordinal)))
                // Migration 0011 lowers the experience curve where the row still holds the old default.
                .Select(row => row.StartsWith("combat_formulas ", StringComparison.Ordinal) ? row.Replace("1000 + level * 23", "100 + level * 50", StringComparison.Ordinal) : row),
            rowsAfter
                .Where(row => unchangedTables.Any(table => row.StartsWith(table, StringComparison.Ordinal)))
                // Migration 0009 seeds the four enemy and armor formulas.
                .Where(row => !row.StartsWith("combat_formulas ", StringComparison.Ordinal) || !WaveINewFormulaKeys.Any(key => row.Contains("formula_key=String:" + key, StringComparison.Ordinal)))
                // Migration 0007 appends player_profiles.account_id, NULL for every existing row.
                .Select(row => row.EndsWith(" account_id=NULL", StringComparison.Ordinal) ? row[..^" account_id=NULL".Length] : row));
        foreach (string table in new[] { "player_world_state", "placed_cube_instances", "cube_fracture_pieces", "enemy_instances", "item_instances", "player_inventory_slots" })
        {
            Assert.Equal(
                rowsBefore.Count(row => row.StartsWith(table + " ", StringComparison.Ordinal)),
                rowsAfter.Count(row => row.StartsWith(table + " ", StringComparison.Ordinal)));
        }

        Assert.Equal([(1L, "baseline"), (2L, "player_starter_grants"), (3L, "worlds"), (4L, "drop_busy_table_updated_utc_indexes"), (5L, "object_ids"), (6L, "terrain"), (7L, "accounts"), (8L, "item_rolls"), (9L, "enemy_formulas"), (10L, "bag_grid"), (11L, "spawn_camps"), (12L, "ground_items_wallets"), (13L, "quickbar_mana"), (14L, "party_quests_admin"), (15L, "attribute_origin"), (16L, "item_visual_ids"), (17L, "enemy_kinds")], SchemaMigrationTests.VersionRows(migrated));
        Assert.Equal(0L, Scalar(migrated, "SELECT COUNT(*) FROM player_starter_grants;"));
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
