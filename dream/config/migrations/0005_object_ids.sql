-- Migration 0005: stable 64-bit object ids.
-- Every persisted world object (placed cube, rubble piece, enemy, item) gets an id from one
-- per-world counter, worlds.next_object_id, that is never reused. The three busy tables are rebuilt
-- with object_id in place of cube_id / source_cube_id / enemy_id (rubble also gets origin_object_id,
-- the retired id of the fractured cube), and cube and rubble owners become nullable with
-- ON DELETE SET NULL so buildings outlive their builder's profile row.
-- Backfill is deterministic, per world: base = MAX(item ids, slot ids) + 1, then ids are assigned in
-- the order cubes (by cube_id), rubble groups (distinct source_cube_id, the origin ids), rubble
-- pieces (by source_cube_id, piece_index), enemies (by enemy_id). Items keep their ids.
-- Each rebuild follows wave B's pattern: create X__v5, copy, DROP X, RENAME X__v5 TO X, recreate
-- the indexes (not the updated_utc ones, which 0004 dropped). The tables stay rowid tables.
-- The four AFTER INSERT triggers keep the counter above every id inserted, in the same statement as
-- the insert. Any later rebuild of these four tables must recreate them.
-- worlds is not rebuilt (it has 7 child foreign keys): the counter is an ALTER TABLE ADD COLUMN.

ALTER TABLE worlds ADD COLUMN next_object_id INTEGER NOT NULL DEFAULT 1;

CREATE TABLE object_id_backfill__v5 (
    world_id INTEGER NOT NULL,
    kind INTEGER NOT NULL,
    key_a INTEGER NOT NULL,
    key_b INTEGER NOT NULL,
    object_id INTEGER NOT NULL,
    PRIMARY KEY (world_id, kind, key_a, key_b)
);

INSERT INTO object_id_backfill__v5 (world_id, kind, key_a, key_b, object_id)
SELECT k.world_id, k.kind, k.key_a, k.key_b,
    COALESCE((SELECT MAX(m) FROM (
        SELECT MAX(item_instance_id) AS m FROM item_instances WHERE world_id = k.world_id
        UNION ALL
        SELECT MAX(item_instance_id) AS m FROM player_inventory_slots WHERE world_id = k.world_id)), 0)
    + ROW_NUMBER() OVER (PARTITION BY k.world_id ORDER BY k.kind, k.key_a, k.key_b)
FROM (
    SELECT world_id, 1 AS kind, cube_id AS key_a, 0 AS key_b FROM placed_cube_instances
    UNION ALL
    SELECT DISTINCT world_id, 2, source_cube_id, 0 FROM cube_fracture_pieces
    UNION ALL
    SELECT world_id, 3, source_cube_id, piece_index FROM cube_fracture_pieces
    UNION ALL
    SELECT world_id, 4, enemy_id, 0 FROM enemy_instances
) AS k;

-- placed_cube_instances
CREATE TABLE placed_cube_instances__v5 (
    world_id INTEGER NOT NULL,
    object_id INTEGER NOT NULL,
    owner_player_name TEXT NULL,
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
    PRIMARY KEY (world_id, object_id),
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    FOREIGN KEY (owner_player_name) REFERENCES player_profiles(player_name) ON DELETE SET NULL,
    CHECK (owner_player_name IS NULL OR length(trim(owner_player_name)) > 0),
    CHECK (current_health >= 0),
    CHECK (max_health >= 1),
    CHECK (current_health <= max_health)
);

INSERT INTO placed_cube_instances__v5 (world_id, object_id, owner_player_name, cell_x, cell_y, cell_z,
    center_x, center_y, center_z, shape_kind, material_id, current_health, max_health, created_utc, updated_utc)
SELECT c.world_id, b.object_id, c.owner_player_name, c.cell_x, c.cell_y, c.cell_z,
    c.center_x, c.center_y, c.center_z, c.shape_kind, c.material_id, c.current_health, c.max_health,
    c.created_utc, c.updated_utc
FROM placed_cube_instances AS c
JOIN object_id_backfill__v5 AS b
    ON b.world_id = c.world_id AND b.kind = 1 AND b.key_a = c.cube_id AND b.key_b = 0
ORDER BY c.world_id, c.cube_id;

DROP TABLE placed_cube_instances;
ALTER TABLE placed_cube_instances__v5 RENAME TO placed_cube_instances;

CREATE INDEX ix_placed_cube_instances_owner_player_name
    ON placed_cube_instances (owner_player_name);

-- cube_fracture_pieces
CREATE TABLE cube_fracture_pieces__v5 (
    world_id INTEGER NOT NULL,
    object_id INTEGER NOT NULL,
    origin_object_id INTEGER NOT NULL,
    piece_index INTEGER NOT NULL,
    owner_player_name TEXT NULL,
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
    PRIMARY KEY (world_id, object_id),
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    FOREIGN KEY (owner_player_name) REFERENCES player_profiles(player_name) ON DELETE SET NULL,
    CHECK (piece_index >= 0),
    CHECK (owner_player_name IS NULL OR length(trim(owner_player_name)) > 0),
    CHECK (side_length > 0)
);

INSERT INTO cube_fracture_pieces__v5 (world_id, object_id, origin_object_id, piece_index, owner_player_name,
    parent_cell_x, parent_cell_y, parent_cell_z, center_x, center_y, center_z, side_length,
    material_id, created_utc, updated_utc)
SELECT p.world_id, bp.object_id, bo.object_id, p.piece_index, p.owner_player_name,
    p.parent_cell_x, p.parent_cell_y, p.parent_cell_z, p.center_x, p.center_y, p.center_z, p.side_length,
    p.material_id, p.created_utc, p.updated_utc
FROM cube_fracture_pieces AS p
JOIN object_id_backfill__v5 AS bp
    ON bp.world_id = p.world_id AND bp.kind = 3 AND bp.key_a = p.source_cube_id AND bp.key_b = p.piece_index
JOIN object_id_backfill__v5 AS bo
    ON bo.world_id = p.world_id AND bo.kind = 2 AND bo.key_a = p.source_cube_id AND bo.key_b = 0
ORDER BY p.world_id, p.source_cube_id, p.piece_index;

DROP TABLE cube_fracture_pieces;
ALTER TABLE cube_fracture_pieces__v5 RENAME TO cube_fracture_pieces;

CREATE UNIQUE INDEX ux_cube_fracture_pieces_origin
    ON cube_fracture_pieces (world_id, origin_object_id, piece_index);

CREATE INDEX ix_cube_fracture_pieces_owner_player_name
    ON cube_fracture_pieces (owner_player_name);

CREATE INDEX ix_cube_fracture_pieces_parent_cell
    ON cube_fracture_pieces (world_id, parent_cell_x, parent_cell_y, parent_cell_z);

-- enemy_instances
CREATE TABLE enemy_instances__v5 (
    world_id INTEGER NOT NULL,
    object_id INTEGER NOT NULL,
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
    PRIMARY KEY (world_id, object_id),
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    CHECK (length(trim(enemy_kind)) > 0),
    CHECK (length(trim(display_name)) > 0),
    CHECK (current_hp >= 0),
    CHECK (max_hp >= 1),
    CHECK (current_hp <= max_hp),
    CHECK (movement_speed >= 0),
    CHECK (attack_speed > 0),
    CHECK (level >= 1)
);

INSERT INTO enemy_instances__v5 (world_id, object_id, enemy_kind, display_name,
    position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp,
    strength, agility, vitality, energy, movement_speed, attack_speed, level, created_utc, updated_utc)
SELECT e.world_id, b.object_id, e.enemy_kind, e.display_name,
    e.position_x, e.position_y, e.position_z, e.facing_x, e.facing_y, e.facing_z, e.current_hp, e.max_hp,
    e.strength, e.agility, e.vitality, e.energy, e.movement_speed, e.attack_speed, e.level,
    e.created_utc, e.updated_utc
FROM enemy_instances AS e
JOIN object_id_backfill__v5 AS b
    ON b.world_id = e.world_id AND b.kind = 4 AND b.key_a = e.enemy_id AND b.key_b = 0
ORDER BY e.world_id, e.enemy_id;

DROP TABLE enemy_instances;
ALTER TABLE enemy_instances__v5 RENAME TO enemy_instances;

-- The counter: above every backfilled id and every item id.
UPDATE worlds SET next_object_id = MAX(
    COALESCE((SELECT MAX(m) FROM (
        SELECT MAX(item_instance_id) AS m FROM item_instances WHERE world_id = worlds.world_id
        UNION ALL
        SELECT MAX(item_instance_id) AS m FROM player_inventory_slots WHERE world_id = worlds.world_id)), 0) + 1,
    COALESCE((SELECT MAX(object_id) FROM object_id_backfill__v5 WHERE world_id = worlds.world_id), 0) + 1);

DROP TABLE object_id_backfill__v5;

-- Counter triggers: same statement as the insert, so the counter is never behind a committed id.
-- An upsert that updates an existing row does not fire an AFTER INSERT trigger.
CREATE TRIGGER trg_placed_cube_instances_advance_object_id AFTER INSERT ON placed_cube_instances BEGIN
    UPDATE worlds SET next_object_id = NEW.object_id + 1
    WHERE world_id = NEW.world_id AND next_object_id <= NEW.object_id;
END;

CREATE TRIGGER trg_cube_fracture_pieces_advance_object_id AFTER INSERT ON cube_fracture_pieces BEGIN
    UPDATE worlds SET next_object_id = NEW.object_id + 1
    WHERE world_id = NEW.world_id AND next_object_id <= NEW.object_id;
END;

CREATE TRIGGER trg_enemy_instances_advance_object_id AFTER INSERT ON enemy_instances BEGIN
    UPDATE worlds SET next_object_id = NEW.object_id + 1
    WHERE world_id = NEW.world_id AND next_object_id <= NEW.object_id;
END;

CREATE TRIGGER trg_item_instances_advance_object_id AFTER INSERT ON item_instances BEGIN
    UPDATE worlds SET next_object_id = NEW.item_instance_id + 1
    WHERE world_id = NEW.world_id AND next_object_id <= NEW.item_instance_id;
END;
