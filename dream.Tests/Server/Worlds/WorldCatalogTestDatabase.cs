using Dream.Server.Persistence;
using Dream.Server.Persistence.Worlds;
using Dream.Tests.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;

namespace Dream.Tests.Server.Worlds;

// A migrated temp database (the shipped migrations, including 0003) for the catalog tests.
internal sealed class WorldCatalogTestDatabase : IDisposable
{
    private readonly MigrationTestDatabase _database = new();

    public WorldCatalogTestDatabase()
    {
        _database.Schema.EnsureSchema();
        // Shipped 0003 seeds world 1; the tests want an empty catalog.
        _database.ExecuteScript("DELETE FROM worlds; DELETE FROM sqlite_sequence WHERE name = 'worlds';");

        Catalog = new SqliteWorldCatalog(_database.Schema);
    }

    public SqliteSchemaBootstrapper Schema => _database.Schema;

    public SqliteWorldCatalog Catalog { get; }

    public long Count(string table, long worldId) =>
        Convert.ToInt64(_database.Scalar($"SELECT COUNT(*) FROM {table} WHERE world_id = {worldId}"));

    public object? Scalar(string sql) => _database.Scalar(sql);

    // Fills every one of the 7 per-world tables of world 1 (creating the world row when absent).
    public void SeedWorldOne()
    {
        _database.ExecuteScript(
            """
            INSERT OR IGNORE INTO worlds (world_id, name, width, height, depth, map_source, generator_key, generator_version, seed, world_time_ticks, created_schema_version)
            VALUES (1, 'default', 56, 8, 56, 'generated', 'default', 1, '12345', 777, 3);
            INSERT INTO player_profiles (player_name, display_name) VALUES ('alice', 'Alice');
            INSERT INTO player_world_state (world_id, player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z)
                VALUES (1, 'alice', 1.5, 2, 3, 0, 0, 1);
            INSERT INTO placed_cube_instances (world_id, cube_id, owner_player_name, cell_x, cell_y, cell_z, material_id, current_health, max_health)
                VALUES (1, 10, 'alice', 4, 1, 5, 'stone', 5, 5), (1, 11, 'alice', 4, 2, 5, 'stone', 5, 5);
            INSERT INTO cube_fracture_pieces (world_id, source_cube_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z, center_x, center_y, center_z, side_length, material_id)
                VALUES (1, 9, 0, 'alice', 3, 1, 3, 3.1, 1.1, 3.1, 0.5, 'stone');
            INSERT INTO enemy_instances (world_id, enemy_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp)
                VALUES (1, 20, 'slime', 'Slime', 0, 0, 0, 0, 0, 1, 10, 10);
            INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, owner_player_name, state)
                VALUES (1, 30, 'tool', 'Pick', 'pick', 'alice', 'Inventory');
            INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id, updated_utc)
                VALUES (1, 'alice', 0, 30, '2026-01-01T00:00:00Z');
            INSERT INTO player_starter_grants (world_id, player_name, kit_version, updated_utc)
                VALUES (1, 'alice', 2, '2026-01-01T00:00:00Z');
            """);
    }

    public static readonly string[] PerWorldTables =
    [
        "player_world_state", "placed_cube_instances", "cube_fracture_pieces", "enemy_instances",
        "item_instances", "player_inventory_slots", "player_starter_grants"
    ];

    public void Execute(string sql) => _database.ExecuteScript(sql);

    public void Dispose() => _database.Dispose();
}
