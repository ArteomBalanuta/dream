using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Persistence wave B (B1): migration 0003 turns the single world into world 1 of a worlds table and
// rebuilds the seven per-world tables with (world_id, ...) keys. The rebuild drops item_instances,
// which would cascade-delete the inventory slots if foreign keys were on, so these tests also pin the
// migrator's foreign-key handling.
public sealed class WorldsMigrationTests
{
    private static readonly string[] PerWorldTables =
    [
        "player_world_state",
        "placed_cube_instances",
        "cube_fracture_pieces",
        "enemy_instances",
        "item_instances",
        "player_inventory_slots",
        "player_starter_grants"
    ];

    // The rows of a world as the server wrote them at version 2.
    private const string VersionTwoRows = """
        INSERT INTO player_profiles (player_name, display_name) VALUES ('alice', 'Alice'), ('bob', 'Bob');
        INSERT INTO player_world_state VALUES ('alice', 1.5, 2.25, -3.125, 0.0, 0.0, 1.0, 80, 9, '2026-01-02T03:04:07.0000000Z');
        INSERT INTO placed_cube_instances (cube_id, owner_player_name, cell_x, cell_y, cell_z, center_x, center_y, center_z,
            shape_kind, material_id, current_health, max_health)
            VALUES (7, 'alice', 1.0, 2.0, 3.0, 1.5, 2.5, 3.5, 'SolidCube', 'Stone', 4, 5),
                   (9, 'bob', 4.0, 2.0, 3.0, NULL, NULL, NULL, 'SolidCube', 'Wood', 5, 5);
        INSERT INTO cube_fracture_pieces (source_cube_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z,
            center_x, center_y, center_z, side_length, material_id)
            VALUES (8, 0, 'alice', 1.0, 2.0, 3.0, 1.25, 2.25, 3.25, 0.5, 'Stone'), (8, 1, 'alice', 1.0, 2.0, 3.0, 1.75, 2.25, 3.25, 0.5, 'Stone');
        INSERT INTO enemy_instances (enemy_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z,
            current_hp, max_hp, level) VALUES (3, 'Goblin', 'Goblin', 4.0, 5.0, 6.0, 1.0, 0.0, 0.0, 10, 20, 2);
        INSERT INTO item_instances (item_instance_id, item_kind, display_name, visual_id, owner_player_name, world_position_x,
            world_position_y, world_position_z, state, stack_count)
            VALUES (11, 'Sword', 'Sword', 'sword_01', 'alice', NULL, NULL, NULL, 'Inventory', 1),
                   (12, 'Potion', 'Potion', 'potion_01', 'bob', NULL, NULL, NULL, 'Inventory', 3),
                   (13, 'Lamp', 'Lamp', 'lamp_01', NULL, 7.0, 8.0, 9.0, 'World', 1);
        INSERT INTO player_inventory_slots (player_name, slot_index, item_instance_id, updated_utc)
            VALUES ('alice', 0, 11, '2026-01-02T03:04:08.000Z'), ('bob', 3, 12, '2026-01-02T03:04:09.000Z');
        INSERT INTO player_starter_grants (player_name, kit_version, updated_utc) VALUES ('alice', 2, '2026-01-02T03:04:10.000Z');
        INSERT INTO world_metadata (key, value) VALUES ('simulation_seed', '18364758544493064720'), ('world_time_ticks', '1234567890123'), ('kept', 'yes');
        """;

    [Fact]
    public void FreshDatabase_HasWorldOne_AndWorldScopedPrimaryKeys()
    {
        using MigrationTestDatabase database = new();

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(new SchemaMigrationResult(0, MigrationTestDatabase.ShippedLatestVersion, null), result);
        using SqliteConnection connection = database.Open();
        Assert.Equal(
            ["1|default|56|8|56|generated|default|1||0|3"],
            Column(connection, """
                SELECT world_id || '|' || name || '|' || width || '|' || height || '|' || depth || '|' || map_source || '|' ||
                    generator_key || '|' || generator_version || '|' || COALESCE(seed, '') || '|' || world_time_ticks || '|' || created_schema_version
                FROM worlds;
                """));
        Assert.Equal(
            [
                "player_world_state|world_id,player_name|0",
                "placed_cube_instances|world_id,object_id|0",
                "cube_fracture_pieces|world_id,object_id|0",
                "enemy_instances|world_id,object_id|0",
                "item_instances|world_id,item_instance_id|0",
                "player_inventory_slots|world_id,player_name,slot_index|0",
                "player_starter_grants|world_id,player_name|0"
            ],
            PerWorldTables.Select(table => table + "|" +
                string.Join(',', Column(connection, $"SELECT name FROM pragma_table_info('{table}') WHERE pk > 0 ORDER BY pk;")) + "|" +
                Column(connection, $"SELECT wr FROM pragma_table_list('{table}');").Single()).ToList());
        Assert.Equal(
            [
                "placed_cube_instances|cell_x|INTEGER", "placed_cube_instances|cell_y|INTEGER", "placed_cube_instances|cell_z|INTEGER",
                "cube_fracture_pieces|parent_cell_x|INTEGER", "cube_fracture_pieces|parent_cell_y|INTEGER", "cube_fracture_pieces|parent_cell_z|INTEGER"
            ],
            Column(connection, """
                SELECT 'placed_cube_instances|' || name || '|' || type FROM pragma_table_info('placed_cube_instances') WHERE name LIKE 'cell\_%' ESCAPE '\'
                UNION ALL
                SELECT 'cube_fracture_pieces|' || name || '|' || type FROM pragma_table_info('cube_fracture_pieces') WHERE name LIKE 'parent\_cell\_%' ESCAPE '\';
                """));
        Assert.Equal(
            [
                "ix_cube_fracture_pieces_owner_player_name", "ix_cube_fracture_pieces_parent_cell",
                "ix_item_instances_ground", "ix_item_instances_owner_player_name", "ix_item_instances_updated_utc",
                "ix_placed_cube_instances_owner_player_name",
                "ix_player_world_state_updated_utc"
            ],
            Column(connection, "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix\\_%' ESCAPE '\\' AND name NOT LIKE 'ix\\_player\\_profile%' ESCAPE '\\' ORDER BY name;"));
        Assert.Equal(
            ["player_inventory_slots|item_instances|CASCADE|world_id,item_instance_id", "player_inventory_slots|player_profiles|CASCADE|player_name", "player_inventory_slots|worlds|CASCADE|world_id"],
            Column(connection, """
                SELECT 'player_inventory_slots|' || "table" || '|' || on_delete || '|' || group_concat("from", ',')
                FROM pragma_foreign_key_list('player_inventory_slots') GROUP BY id, "table" ORDER BY "table";
                """));
        // The worlds table refuses a duplicate name ignoring case, and ids are never reused.
        Assert.Throws<SqliteException>(() => Execute(connection, """
            INSERT INTO worlds (name, width, height, depth, map_source, generator_key, generator_version, created_schema_version)
            VALUES ('DEFAULT', 20, 8, 20, 'generated', 'default', 1, 3);
            """));
        Assert.Equal("wal", database.Scalar("PRAGMA journal_mode;"));
    }

    [Fact]
    public void VersionTwoDatabaseWithData_Migrates_EveryRowSurvives_IncludingTheInventorySlots()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpToTheStarterGrants());
        database.ExecuteScript(VersionTwoRows);
        MigrationTestDatabase.ReleasePooledConnections();

        // Foreign keys ON, as every runtime connection has them: dropping item_instances must not
        // cascade into the slots.
        List<string> log = [];
        using (SqliteConnection connection = database.Schema.CreateOpenConnection())
        {
            Execute(connection, "PRAGMA foreign_keys = ON;");
            SchemaMigrationResult result = new SqliteSchemaMigrator(SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory))
                .Migrate(database.DatabasePath, connection, log.Add);

            Assert.Equal(2, result.FromVersion);
            Assert.Equal(MigrationTestDatabase.ShippedLatestVersion, result.ToVersion);
            // The pragma is back to what the caller had.
            Assert.Equal(1L, Scalar(connection, "PRAGMA foreign_keys;"));
        }

        string backup = Assert.Single(database.Backups());
        Assert.Matches(@"persistence\.sqlite\.v0002-to-v0015\.\d{8}T\d{6}Z\.bak$", backup);
        using SqliteConnection migrated = database.Open();
        Assert.Equal([(1L, "baseline"), (2L, "player_starter_grants"), (3L, "worlds"), (4L, "drop_busy_table_updated_utc_indexes"), (5L, "object_ids"), (6L, "terrain"), (7L, "accounts"), (8L, "item_rolls"), (9L, "enemy_formulas"), (10L, "bag_grid"), (11L, "spawn_camps"), (12L, "ground_items_wallets"), (13L, "quickbar_mana"), (14L, "party_quests_admin"), (15L, "attribute_origin")], SchemaMigrationTests.VersionRows(migrated));
        Assert.Equal(2L, Scalar(migrated, "SELECT COUNT(*) FROM player_inventory_slots;"));
        Assert.Equal(
            // Migration 0010 moved old slot 3 (row 0, column 3 of the 4x4 backpack) to anchor 6 of the 8x8 board.
            ["alice|0|11|Sword", "bob|6|12|Potion"],
            Column(migrated, """
                SELECT s.player_name || '|' || s.slot_index || '|' || s.item_instance_id || '|' || i.item_kind
                FROM player_inventory_slots AS s JOIN item_instances AS i ON i.world_id = s.world_id AND i.item_instance_id = s.item_instance_id
                WHERE s.world_id = 1 ORDER BY s.player_name;
                """));
        Assert.Equal(3L, Scalar(migrated, "SELECT COUNT(*) FROM item_instances WHERE world_id = 1;"));
        Assert.Equal(2L, Scalar(migrated, "SELECT COUNT(*) FROM placed_cube_instances WHERE world_id = 1;"));
        Assert.Equal(2L, Scalar(migrated, "SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1;"));
        Assert.Equal(1L, Scalar(migrated, "SELECT COUNT(*) FROM enemy_instances WHERE world_id = 1;"));
        Assert.Equal(1L, Scalar(migrated, "SELECT COUNT(*) FROM player_world_state WHERE world_id = 1;"));
        Assert.Equal(1L, Scalar(migrated, "SELECT COUNT(*) FROM player_starter_grants WHERE world_id = 1;"));
        foreach (string table in PerWorldTables)
        {
            Assert.Equal(0L, Scalar(migrated, $"SELECT COUNT(*) FROM \"{table}\" WHERE world_id <> 1;"));
        }

        // Whole REAL cells become INTEGER without loss; the other columns are copied as they were.
        Assert.Equal(["integer|1|2|3", "integer|4|2|3"], Column(migrated, "SELECT typeof(cell_x) || '|' || cell_x || '|' || cell_y || '|' || cell_z FROM placed_cube_instances ORDER BY object_id;"));
        Assert.Equal(["1.5|2.5|3.5", "||"], Column(migrated, "SELECT COALESCE(center_x, '') || '|' || COALESCE(center_y, '') || '|' || COALESCE(center_z, '') FROM placed_cube_instances ORDER BY object_id;"));
        Assert.Equal(["integer|3"], Column(migrated, "SELECT DISTINCT typeof(parent_cell_z) || '|' || parent_cell_z FROM cube_fracture_pieces;"));
        Assert.Equal(["7.0|8.0|9.0"], Column(migrated, "SELECT world_position_x || '|' || world_position_y || '|' || world_position_z FROM item_instances WHERE item_instance_id = 13;"));
        Assert.Equal(["alice|2|2026-01-02T03:04:10.000Z"], Column(migrated, "SELECT player_name || '|' || kit_version || '|' || updated_utc FROM player_starter_grants;"));

        // The seed and the clock moved onto the world 1 row; other metadata stays.
        Assert.Equal("18364758544493064720", Scalar(migrated, "SELECT seed FROM worlds WHERE world_id = 1;"));
        Assert.Equal(1_234_567_890_123L, Scalar(migrated, "SELECT world_time_ticks FROM worlds WHERE world_id = 1;"));
        Assert.Equal(["kept"], Column(migrated, "SELECT key FROM world_metadata;"));
        Assert.Empty(Column(migrated, "PRAGMA foreign_key_check;"));
        Assert.Equal("ok", Scalar(migrated, "PRAGMA integrity_check;"));
        Assert.Equal("wal", Scalar(migrated, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void VersionTwoDatabaseWithoutSeedOrClock_GetsANullSeedAndTimeZero()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpToTheStarterGrants());

        database.Schema.EnsureSchema();

        using SqliteConnection migrated = database.Open();
        Assert.Equal(["|0"], Column(migrated, "SELECT COALESCE(seed, '') || '|' || world_time_ticks FROM worlds WHERE world_id = 1;"));
    }

    [Fact]
    public void OrphanRow_FailsTheMigrationLoudly_RollsBack_AndKeepsVersionTwo()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpToTheStarterGrants());
        // A cube whose owner has no profile: possible in a database written with foreign keys off.
        database.ExecuteScript("""
            PRAGMA foreign_keys = OFF;
            INSERT INTO placed_cube_instances (cube_id, owner_player_name, cell_x, cell_y, cell_z, shape_kind, material_id, current_health, max_health)
                VALUES (7, 'ghost', 1.0, 2.0, 3.0, 'SolidCube', 'Stone', 4, 5);
            """);
        MigrationTestDatabase.ReleasePooledConnections();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => database.Schema.EnsureSchema());

        Assert.Contains("placed_cube_instances", error.Message, StringComparison.Ordinal);
        Assert.Contains("player_profiles", error.Message, StringComparison.Ordinal);
        Assert.Contains("rolled back", error.Message, StringComparison.Ordinal);
        using SqliteConnection connection = database.Open();
        Assert.Equal([(1L, "baseline"), (2L, "player_starter_grants")], SchemaMigrationTests.VersionRows(connection));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('worlds', 'placed_cube_instances__v3');"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM placed_cube_instances;"));
        // The backup taken before the attempt is still there.
        Assert.Single(database.Backups());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForeignKeysPragma_IsRestoredToWhatTheCallerHad_AfterASuccessAndAFailure(bool foreignKeysOn)
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpToTheStarterGrants());
        MigrationTestDatabase.ReleasePooledConnections();
        SqliteSchemaMigrator migrator = new(SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory));
        string pragma = foreignKeysOn ? "ON" : "OFF";
        long expected = foreignKeysOn ? 1L : 0L;

        // After a failure (an orphan row, written with foreign keys off).
        database.ExecuteScript("""
            PRAGMA foreign_keys = OFF;
            INSERT INTO placed_cube_instances (cube_id, owner_player_name, cell_x, cell_y, cell_z, shape_kind, material_id, current_health, max_health)
                VALUES (7, 'ghost', 1.0, 2.0, 3.0, 'SolidCube', 'Stone', 4, 5);
            """);
        using (SqliteConnection connection = MigrationTestDatabase.OpenFile(database.DatabasePath))
        {
            Execute(connection, $"PRAGMA foreign_keys = {pragma};");
            Assert.Throws<InvalidOperationException>(() => migrator.Migrate(database.DatabasePath, connection));
            Assert.Equal(expected, Scalar(connection, "PRAGMA foreign_keys;"));
        }

        // After a success.
        database.ExecuteScript("PRAGMA foreign_keys = OFF; DELETE FROM placed_cube_instances;");
        using (SqliteConnection connection = MigrationTestDatabase.OpenFile(database.DatabasePath))
        {
            Execute(connection, $"PRAGMA foreign_keys = {pragma};");
            Assert.Equal(MigrationTestDatabase.ShippedLatestVersion, migrator.Migrate(database.DatabasePath, connection).ToVersion);
            Assert.Equal(expected, Scalar(connection, "PRAGMA foreign_keys;"));
        }
    }

    private static SqliteSchemaMigrator UpToTheStarterGrants() =>
        new([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(2)]);

    private static void Execute(SqliteConnection connection, string sql) => MigrationTestDatabase.Execute(connection, sql);

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static List<string> Column(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> values = [];
        while (reader.Read())
        {
            values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return values;
    }
}
