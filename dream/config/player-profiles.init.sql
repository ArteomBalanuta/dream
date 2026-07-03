-- Server-owned persistence schema for multiplayer player, world, enemy, and item state.
-- Identity is keyed by player name in the current first-pass design.
-- The schema is intentionally denormalized so the authoritative host can restore
-- active world state quickly on startup and on reconnect.

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS player_profiles (
    player_name TEXT NOT NULL PRIMARY KEY,
    display_name TEXT NOT NULL,
    interaction_mode TEXT NOT NULL DEFAULT 'Building',
    strength INTEGER NOT NULL DEFAULT 10,
    agility INTEGER NOT NULL DEFAULT 10,
    vitality INTEGER NOT NULL DEFAULT 10,
    energy INTEGER NOT NULL DEFAULT 10,
    movement_speed REAL NOT NULL DEFAULT 6.0,
    attack_speed REAL NOT NULL DEFAULT 2.0,
    level INTEGER NOT NULL DEFAULT 1,
    current_hp INTEGER NOT NULL DEFAULT 100,
    max_hp INTEGER NOT NULL DEFAULT 100,
    created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    CHECK (length(trim(player_name)) > 0),
    CHECK (length(trim(display_name)) > 0),
    CHECK (interaction_mode IN ('Building', 'Combat')),
    CHECK (strength >= 1),
    CHECK (agility >= 0),
    CHECK (vitality >= 1),
    CHECK (energy >= 0),
    CHECK (movement_speed > 0),
    CHECK (attack_speed > 0),
    CHECK (level >= 1),
    CHECK (max_hp >= 1),
    CHECK (current_hp >= 0),
    CHECK (current_hp <= max_hp)
);

CREATE INDEX IF NOT EXISTS ix_player_profiles_updated_utc
    ON player_profiles (updated_utc);

CREATE TABLE IF NOT EXISTS player_world_state (
    player_name TEXT NOT NULL PRIMARY KEY,
    position_x REAL NOT NULL,
    position_y REAL NOT NULL,
    position_z REAL NOT NULL,
    facing_x REAL NOT NULL,
    facing_y REAL NOT NULL,
    facing_z REAL NOT NULL,
    current_hp INTEGER NOT NULL DEFAULT 100,
    current_energy INTEGER NOT NULL DEFAULT 10,
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
    CHECK (length(trim(player_name)) > 0)
);

CREATE INDEX IF NOT EXISTS ix_player_world_state_updated_utc
    ON player_world_state (updated_utc);

CREATE TABLE IF NOT EXISTS placed_cube_instances (
    cube_id INTEGER NOT NULL PRIMARY KEY,
    owner_player_name TEXT NOT NULL,
    cell_x REAL NOT NULL,
    cell_y REAL NOT NULL,
    cell_z REAL NOT NULL,
    shape_kind TEXT NOT NULL DEFAULT 'SolidCube',
    material_id TEXT NOT NULL,
    current_health INTEGER NOT NULL,
    max_health INTEGER NOT NULL,
    created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    FOREIGN KEY (owner_player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
    CHECK (length(trim(owner_player_name)) > 0),
    CHECK (current_health >= 0),
    CHECK (max_health >= 1),
    CHECK (current_health <= max_health)
);

CREATE INDEX IF NOT EXISTS ix_placed_cube_instances_owner_player_name
    ON placed_cube_instances (owner_player_name);

CREATE INDEX IF NOT EXISTS ix_placed_cube_instances_updated_utc
    ON placed_cube_instances (updated_utc);

CREATE TABLE IF NOT EXISTS enemy_instances (
    enemy_id INTEGER NOT NULL PRIMARY KEY,
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
    CHECK (length(trim(enemy_kind)) > 0),
    CHECK (length(trim(display_name)) > 0),
    CHECK (current_hp >= 0),
    CHECK (max_hp >= 1),
    CHECK (current_hp <= max_hp),
    CHECK (movement_speed >= 0),
    CHECK (attack_speed > 0),
    CHECK (level >= 1)
);

CREATE INDEX IF NOT EXISTS ix_enemy_instances_updated_utc
    ON enemy_instances (updated_utc);

CREATE TABLE IF NOT EXISTS item_instances (
    item_instance_id INTEGER NOT NULL PRIMARY KEY,
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
    FOREIGN KEY (owner_player_name) REFERENCES player_profiles(player_name) ON DELETE SET NULL,
    CHECK (length(trim(item_kind)) > 0),
    CHECK (length(trim(display_name)) > 0),
    CHECK (length(trim(visual_id)) > 0),
    CHECK (length(trim(state)) > 0),
    CHECK (stack_count >= 1)
);

CREATE INDEX IF NOT EXISTS ix_item_instances_owner_player_name
    ON item_instances (owner_player_name);

CREATE INDEX IF NOT EXISTS ix_item_instances_updated_utc
    ON item_instances (updated_utc);

CREATE TABLE IF NOT EXISTS player_inventory_slots (
    player_name TEXT NOT NULL,
    slot_index INTEGER NOT NULL,
    item_instance_id INTEGER NOT NULL,
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    PRIMARY KEY (player_name, slot_index),
    FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
    FOREIGN KEY (item_instance_id) REFERENCES item_instances(item_instance_id) ON DELETE CASCADE,
    CHECK (length(trim(player_name)) > 0),
    CHECK (slot_index >= 0)
);

CREATE INDEX IF NOT EXISTS ix_player_inventory_slots_item_instance_id
    ON player_inventory_slots (item_instance_id);

CREATE TABLE IF NOT EXISTS player_profile_change_log (
    change_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    player_name TEXT NOT NULL,
    change_kind TEXT NOT NULL,
    previous_interaction_mode TEXT NULL,
    new_interaction_mode TEXT NULL,
    previous_strength INTEGER NULL,
    new_strength INTEGER NULL,
    previous_attack_speed REAL NULL,
    new_attack_speed REAL NULL,
    previous_vitality INTEGER NULL,
    new_vitality INTEGER NULL,
    changed_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
    CHECK (length(trim(player_name)) > 0),
    CHECK (length(trim(change_kind)) > 0)
);

CREATE INDEX IF NOT EXISTS ix_player_profile_change_log_player_name_changed_utc
    ON player_profile_change_log (player_name, changed_utc DESC);

CREATE TRIGGER IF NOT EXISTS trg_player_profiles_set_updated_utc
AFTER UPDATE ON player_profiles
FOR EACH ROW
WHEN NEW.updated_utc = OLD.updated_utc
BEGIN
    UPDATE player_profiles
    SET updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE player_name = NEW.player_name;
END;
