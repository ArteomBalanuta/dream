-- Migration 0003: worlds as rows.
-- Adds the worlds table (world 1 = the existing data), moves the simulation seed and the world
-- clock from world_metadata onto it, and rebuilds the seven per-world tables with a world_id
-- column first in their primary keys. Global tables (player_profiles, player_profile_change_log,
-- combat_formulas, schema_version, world_metadata) are untouched.
-- Each rebuild follows SQLite's 12-step procedure: create X__v3, copy, DROP X, RENAME X__v3 TO X,
-- recreate the indexes. The old table is never renamed away first (that would rewrite the child
-- foreign keys). The migrator runs with foreign_keys OFF (otherwise dropping item_instances would
-- cascade-delete the inventory slots) and runs foreign_key_check before it commits.

CREATE TABLE worlds (
    world_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    width INTEGER NOT NULL,
    height INTEGER NOT NULL,
    depth INTEGER NOT NULL,
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

INSERT INTO worlds (world_id, name, width, height, depth, map_source, generator_key, generator_version,
                    seed, world_time_ticks, created_schema_version)
VALUES (1, 'default', 56, 8, 56, 'generated', 'default', 1,
        (SELECT value FROM world_metadata WHERE key = 'simulation_seed'),
        COALESCE((SELECT value FROM world_metadata WHERE key = 'world_time_ticks'), 0), 3);

DELETE FROM world_metadata WHERE key IN ('simulation_seed', 'world_time_ticks');

-- player_world_state
CREATE TABLE player_world_state__v3 (
    world_id INTEGER NOT NULL,
    player_name TEXT NOT NULL,
    position_x REAL NOT NULL,
    position_y REAL NOT NULL,
    position_z REAL NOT NULL,
    facing_x REAL NOT NULL,
    facing_y REAL NOT NULL,
    facing_z REAL NOT NULL,
    current_hp INTEGER NOT NULL DEFAULT 100,
    current_energy INTEGER NOT NULL DEFAULT 10,
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    PRIMARY KEY (world_id, player_name),
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
    CHECK (length(trim(player_name)) > 0)
);

INSERT INTO player_world_state__v3 (world_id, player_name, position_x, position_y, position_z,
    facing_x, facing_y, facing_z, current_hp, current_energy, updated_utc)
SELECT 1, player_name, position_x, position_y, position_z,
    facing_x, facing_y, facing_z, current_hp, current_energy, updated_utc
FROM player_world_state;

DROP TABLE player_world_state;
ALTER TABLE player_world_state__v3 RENAME TO player_world_state;

CREATE INDEX ix_player_world_state_updated_utc
    ON player_world_state (updated_utc);

-- placed_cube_instances
CREATE TABLE placed_cube_instances__v3 (
    world_id INTEGER NOT NULL,
    cube_id INTEGER NOT NULL,
    owner_player_name TEXT NOT NULL,
    cell_x INTEGER NOT NULL,
    cell_y INTEGER NOT NULL,
    cell_z INTEGER NOT NULL,
    center_x REAL NULL,
    center_y REAL NULL,
    center_z REAL NULL,
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
    CHECK (current_health >= 0),
    CHECK (max_health >= 1),
    CHECK (current_health <= max_health)
) WITHOUT ROWID;

INSERT INTO placed_cube_instances__v3 (world_id, cube_id, owner_player_name, cell_x, cell_y, cell_z,
    center_x, center_y, center_z, shape_kind, material_id, current_health, max_health, created_utc, updated_utc)
SELECT 1, cube_id, owner_player_name, cell_x, cell_y, cell_z,
    center_x, center_y, center_z, shape_kind, material_id, current_health, max_health, created_utc, updated_utc
FROM placed_cube_instances;

DROP TABLE placed_cube_instances;
ALTER TABLE placed_cube_instances__v3 RENAME TO placed_cube_instances;

CREATE INDEX ix_placed_cube_instances_owner_player_name
    ON placed_cube_instances (owner_player_name);

CREATE INDEX ix_placed_cube_instances_updated_utc
    ON placed_cube_instances (updated_utc);

-- cube_fracture_pieces
CREATE TABLE cube_fracture_pieces__v3 (
    world_id INTEGER NOT NULL,
    source_cube_id INTEGER NOT NULL,
    piece_index INTEGER NOT NULL,
    owner_player_name TEXT NOT NULL,
    parent_cell_x INTEGER NOT NULL,
    parent_cell_y INTEGER NOT NULL,
    parent_cell_z INTEGER NOT NULL,
    center_x REAL NOT NULL,
    center_y REAL NOT NULL,
    center_z REAL NOT NULL,
    side_length REAL NOT NULL,
    material_id TEXT NOT NULL,
    created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    PRIMARY KEY (world_id, source_cube_id, piece_index),
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    FOREIGN KEY (owner_player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
    CHECK (piece_index >= 0),
    CHECK (length(trim(owner_player_name)) > 0),
    CHECK (side_length > 0)
) WITHOUT ROWID;

INSERT INTO cube_fracture_pieces__v3 (world_id, source_cube_id, piece_index, owner_player_name,
    parent_cell_x, parent_cell_y, parent_cell_z, center_x, center_y, center_z, side_length,
    material_id, created_utc, updated_utc)
SELECT 1, source_cube_id, piece_index, owner_player_name,
    parent_cell_x, parent_cell_y, parent_cell_z, center_x, center_y, center_z, side_length,
    material_id, created_utc, updated_utc
FROM cube_fracture_pieces;

DROP TABLE cube_fracture_pieces;
ALTER TABLE cube_fracture_pieces__v3 RENAME TO cube_fracture_pieces;

CREATE INDEX ix_cube_fracture_pieces_owner_player_name
    ON cube_fracture_pieces (owner_player_name);

CREATE INDEX ix_cube_fracture_pieces_parent_cell
    ON cube_fracture_pieces (world_id, parent_cell_x, parent_cell_y, parent_cell_z);

CREATE INDEX ix_cube_fracture_pieces_updated_utc
    ON cube_fracture_pieces (updated_utc);

-- enemy_instances
CREATE TABLE enemy_instances__v3 (
    world_id INTEGER NOT NULL,
    enemy_id INTEGER NOT NULL,
    enemy_kind TEXT NOT NULL,
    display_name TEXT NOT NULL,
    position_x REAL NOT NULL,
    position_y REAL NOT NULL,
    position_z REAL NOT NULL,
    facing_x REAL NOT NULL,
    facing_y REAL NOT NULL,
    facing_z REAL NOT NULL,
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
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    CHECK (length(trim(enemy_kind)) > 0),
    CHECK (length(trim(display_name)) > 0),
    CHECK (current_hp >= 0),
    CHECK (max_hp >= 1),
    CHECK (current_hp <= max_hp),
    CHECK (movement_speed >= 0),
    CHECK (attack_speed > 0),
    CHECK (level >= 1)
) WITHOUT ROWID;

INSERT INTO enemy_instances__v3 (world_id, enemy_id, enemy_kind, display_name,
    position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp,
    strength, agility, vitality, energy, movement_speed, attack_speed, level, created_utc, updated_utc)
SELECT 1, enemy_id, enemy_kind, display_name,
    position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp,
    strength, agility, vitality, energy, movement_speed, attack_speed, level, created_utc, updated_utc
FROM enemy_instances;

DROP TABLE enemy_instances;
ALTER TABLE enemy_instances__v3 RENAME TO enemy_instances;

CREATE INDEX ix_enemy_instances_updated_utc
    ON enemy_instances (updated_utc);

-- item_instances
CREATE TABLE item_instances__v3 (
    world_id INTEGER NOT NULL,
    item_instance_id INTEGER NOT NULL,
    item_kind TEXT NOT NULL,
    display_name TEXT NOT NULL,
    visual_id TEXT NOT NULL,
    owner_player_name TEXT NULL,
    world_position_x REAL NULL,
    world_position_y REAL NULL,
    world_position_z REAL NULL,
    state TEXT NOT NULL,
    stack_count INTEGER NOT NULL DEFAULT 1,
    created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    PRIMARY KEY (world_id, item_instance_id),
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    FOREIGN KEY (owner_player_name) REFERENCES player_profiles(player_name) ON DELETE SET NULL,
    CHECK (length(trim(item_kind)) > 0),
    CHECK (length(trim(display_name)) > 0),
    CHECK (length(trim(visual_id)) > 0),
    CHECK (length(trim(state)) > 0),
    CHECK (stack_count >= 1)
);

INSERT INTO item_instances__v3 (world_id, item_instance_id, item_kind, display_name, visual_id,
    owner_player_name, world_position_x, world_position_y, world_position_z, state, stack_count,
    created_utc, updated_utc)
SELECT 1, item_instance_id, item_kind, display_name, visual_id,
    owner_player_name, world_position_x, world_position_y, world_position_z, state, stack_count,
    created_utc, updated_utc
FROM item_instances;

DROP TABLE item_instances;
ALTER TABLE item_instances__v3 RENAME TO item_instances;

CREATE INDEX ix_item_instances_owner_player_name
    ON item_instances (owner_player_name);

CREATE INDEX ix_item_instances_updated_utc
    ON item_instances (updated_utc);

-- player_inventory_slots
CREATE TABLE player_inventory_slots__v3 (
    world_id INTEGER NOT NULL,
    player_name TEXT NOT NULL,
    slot_index INTEGER NOT NULL,
    item_instance_id INTEGER NOT NULL,
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    PRIMARY KEY (world_id, player_name, slot_index),
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
    FOREIGN KEY (world_id, item_instance_id) REFERENCES item_instances(world_id, item_instance_id) ON DELETE CASCADE,
    CHECK (length(trim(player_name)) > 0),
    CHECK (slot_index >= 0)
);

INSERT INTO player_inventory_slots__v3 (world_id, player_name, slot_index, item_instance_id, updated_utc)
SELECT 1, player_name, slot_index, item_instance_id, updated_utc
FROM player_inventory_slots;

DROP TABLE player_inventory_slots;
ALTER TABLE player_inventory_slots__v3 RENAME TO player_inventory_slots;

CREATE INDEX ix_player_inventory_slots_item_instance_id
    ON player_inventory_slots (world_id, item_instance_id);

-- player_starter_grants
CREATE TABLE player_starter_grants__v3 (
    world_id INTEGER NOT NULL,
    player_name TEXT NOT NULL,
    kit_version INTEGER NOT NULL,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (world_id, player_name),
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
    CHECK (length(trim(player_name)) > 0),
    CHECK (kit_version >= 0)
);

INSERT INTO player_starter_grants__v3 (world_id, player_name, kit_version, updated_utc)
SELECT 1, player_name, kit_version, updated_utc
FROM player_starter_grants;

DROP TABLE player_starter_grants;
ALTER TABLE player_starter_grants__v3 RENAME TO player_starter_grants;
