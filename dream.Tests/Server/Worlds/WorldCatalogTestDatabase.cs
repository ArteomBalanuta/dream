using Dream.Server.Persistence;
using Dream.Server.Persistence.Worlds;
using Dream.Tests.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;

namespace Dream.Tests.Server.Worlds;

// A migrated temp database for the catalog tests. While the shipped migrations stop before 0003
// (workstream B1), the plan's 0003 DDL is applied on top of 0001/0002 here; once 0003 ships the
// `worlds` table already exists and this fixture only seeds data. After B1 merges, the
// TestOnlyWorldsDdl block below can be deleted.
internal sealed class WorldCatalogTestDatabase : IDisposable
{
    private readonly MigrationTestDatabase _database = new();

    public WorldCatalogTestDatabase()
    {
        _database.Schema.EnsureSchema();
        if (Convert.ToInt64(_database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'worlds'")) == 0)
        {
            _database.ExecuteScript(TestOnlyWorldsDdl);
        }
        else
        {
            // Shipped 0003 seeds world 1; the tests want an empty catalog in both modes.
            WorldsTableIsShipped = true;
            _database.ExecuteScript("DELETE FROM worlds; DELETE FROM sqlite_sequence WHERE name = 'worlds';");
        }

        Catalog = new SqliteWorldCatalog(_database.Schema);
    }

    public bool WorldsTableIsShipped { get; }

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

    // Test-only copy of the plan's migration 0003 for an empty database (no row copying needed).
    private const string TestOnlyWorldsDdl =
        """
        CREATE TABLE worlds (
            world_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            width INTEGER NOT NULL, height INTEGER NOT NULL, depth INTEGER NOT NULL,
            map_source TEXT NOT NULL,
            generator_key TEXT NOT NULL,
            generator_version INTEGER NOT NULL,
            seed TEXT NULL,
            world_time_ticks INTEGER NOT NULL DEFAULT 0,
            created_schema_version INTEGER NOT NULL,
            created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            CHECK (length(trim(name)) > 0),
            CHECK (width >= 1 AND height >= 1 AND depth >= 1),
            CHECK (map_source IN ('generated', 'stored')),
            CHECK (length(trim(generator_key)) > 0),
            CHECK (generator_version >= 1),
            CHECK (created_schema_version >= 1)
        );
        CREATE UNIQUE INDEX ux_worlds_name ON worlds (name COLLATE NOCASE);

        DROP TABLE player_inventory_slots;
        DROP TABLE player_starter_grants;
        DROP TABLE item_instances;
        DROP TABLE enemy_instances;
        DROP TABLE cube_fracture_pieces;
        DROP TABLE placed_cube_instances;
        DROP TABLE player_world_state;

        CREATE TABLE player_world_state (
            world_id INTEGER NOT NULL,
            player_name TEXT NOT NULL,
            position_x REAL NOT NULL, position_y REAL NOT NULL, position_z REAL NOT NULL,
            facing_x REAL NOT NULL, facing_y REAL NOT NULL, facing_z REAL NOT NULL,
            current_hp INTEGER NOT NULL DEFAULT 100,
            current_energy INTEGER NOT NULL DEFAULT 10,
            updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            PRIMARY KEY (world_id, player_name),
            FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
            FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
            CHECK (length(trim(player_name)) > 0)
        );
        CREATE TABLE placed_cube_instances (
            world_id INTEGER NOT NULL,
            cube_id INTEGER NOT NULL,
            owner_player_name TEXT NOT NULL,
            cell_x INTEGER NOT NULL, cell_y INTEGER NOT NULL, cell_z INTEGER NOT NULL,
            center_x REAL NULL, center_y REAL NULL, center_z REAL NULL,
            shape_kind TEXT NOT NULL DEFAULT 'SolidCube',
            material_id TEXT NOT NULL,
            current_health INTEGER NOT NULL,
            max_health INTEGER NOT NULL,
            created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            PRIMARY KEY (world_id, cube_id),
            FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
            FOREIGN KEY (owner_player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
            CHECK (length(trim(owner_player_name)) > 0),
            CHECK (current_health >= 0), CHECK (max_health >= 1), CHECK (current_health <= max_health)
        ) WITHOUT ROWID;
        CREATE TABLE cube_fracture_pieces (
            world_id INTEGER NOT NULL,
            source_cube_id INTEGER NOT NULL,
            piece_index INTEGER NOT NULL,
            owner_player_name TEXT NOT NULL,
            parent_cell_x INTEGER NOT NULL, parent_cell_y INTEGER NOT NULL, parent_cell_z INTEGER NOT NULL,
            center_x REAL NOT NULL, center_y REAL NOT NULL, center_z REAL NOT NULL,
            side_length REAL NOT NULL,
            material_id TEXT NOT NULL,
            created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            PRIMARY KEY (world_id, source_cube_id, piece_index),
            FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
            FOREIGN KEY (owner_player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
            CHECK (piece_index >= 0), CHECK (length(trim(owner_player_name)) > 0), CHECK (side_length > 0)
        ) WITHOUT ROWID;
        CREATE TABLE enemy_instances (
            world_id INTEGER NOT NULL,
            enemy_id INTEGER NOT NULL,
            enemy_kind TEXT NOT NULL,
            display_name TEXT NOT NULL,
            position_x REAL NOT NULL, position_y REAL NOT NULL, position_z REAL NOT NULL,
            facing_x REAL NOT NULL, facing_y REAL NOT NULL, facing_z REAL NOT NULL,
            current_hp INTEGER NOT NULL,
            max_hp INTEGER NOT NULL,
            strength INTEGER NOT NULL DEFAULT 10,
            agility INTEGER NOT NULL DEFAULT 10,
            vitality INTEGER NOT NULL DEFAULT 10,
            energy INTEGER NOT NULL DEFAULT 0,
            movement_speed REAL NOT NULL DEFAULT 2.8,
            attack_speed REAL NOT NULL DEFAULT 0.95,
            level INTEGER NOT NULL DEFAULT 1,
            created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            PRIMARY KEY (world_id, enemy_id),
            FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE
        ) WITHOUT ROWID;
        CREATE TABLE item_instances (
            world_id INTEGER NOT NULL,
            item_instance_id INTEGER NOT NULL,
            item_kind TEXT NOT NULL,
            display_name TEXT NOT NULL,
            visual_id TEXT NOT NULL,
            owner_player_name TEXT NULL,
            world_position_x REAL NULL, world_position_y REAL NULL, world_position_z REAL NULL,
            state TEXT NOT NULL,
            stack_count INTEGER NOT NULL DEFAULT 1,
            created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            PRIMARY KEY (world_id, item_instance_id),
            FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
            FOREIGN KEY (owner_player_name) REFERENCES player_profiles(player_name) ON DELETE SET NULL
        );
        CREATE TABLE player_inventory_slots (
            world_id INTEGER NOT NULL,
            player_name TEXT NOT NULL,
            slot_index INTEGER NOT NULL,
            item_instance_id INTEGER NOT NULL,
            updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            PRIMARY KEY (world_id, player_name, slot_index),
            FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
            FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
            FOREIGN KEY (world_id, item_instance_id) REFERENCES item_instances(world_id, item_instance_id) ON DELETE CASCADE
        );
        CREATE TABLE player_starter_grants (
            world_id INTEGER NOT NULL,
            player_name TEXT NOT NULL,
            kit_version INTEGER NOT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (world_id, player_name),
            FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
            FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE
        );
        """;
}
