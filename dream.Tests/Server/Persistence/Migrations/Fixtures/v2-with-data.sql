-- Fixture: the rows of a schema version 2 database that has been played on, to be applied on top of
-- a database built with migrations 0001 and 0002 only (V2DatabaseWithDataStartupTests). Every
-- world-scoped table has rows, plus the seed and the world time in world_metadata. Migration 0003
-- must move all of it to world 1 and onto the worlds row without losing a row.

INSERT INTO player_profiles (player_name, display_name, interaction_mode, strength, agility, vitality, energy,
    movement_speed, attack_speed, level, experience, current_hp, max_hp, created_utc, updated_utc)
VALUES ('Name legacy', 'Name legacy', 'Building', 10, 10, 10, 10, 5.0, 1.0, 1, 0, 100, 100,
        '2026-01-02T03:04:05.123Z', '2026-01-02T03:04:06.123Z');

INSERT INTO player_world_state (player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z,
    current_hp, current_energy, updated_utc)
VALUES ('Name legacy', 30.5, 1.5, 30.5, 0.0, 0.0, 1.0, 100, 10, '2026-01-02T03:04:07.123Z');

-- Whole REAL cell coordinates: migration 0003 stores them as INTEGER without loss.
INSERT INTO placed_cube_instances (cube_id, owner_player_name, cell_x, cell_y, cell_z, center_x, center_y, center_z,
    shape_kind, material_id, current_health, max_health)
VALUES (7, 'Name legacy', 20.0, 1.0, 20.0, 20.5, 1.5, 20.5, 'SolidCube', 'Stone', 5, 5),
       (8, 'Name legacy', 22.0, 1.0, 20.0, 22.5, 1.5, 20.5, 'SolidCube', 'Lamp', 3, 3);

INSERT INTO cube_fracture_pieces (source_cube_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z,
    center_x, center_y, center_z, side_length, material_id)
VALUES (9, 0, 'Name legacy', 24.0, 1.0, 20.0, 24.25, 1.25, 20.25, 0.5, 'Stone'),
       (9, 1, 'Name legacy', 24.0, 1.0, 20.0, 24.75, 1.25, 20.25, 0.5, 'Stone');

INSERT INTO enemy_instances (enemy_id, enemy_kind, display_name, position_x, position_y, position_z,
    facing_x, facing_y, facing_z, current_hp, max_hp)
VALUES (4301, 'Wolf', 'Wolf', 40.5, 1.0, 12.5, 0.0, 0.0, 1.0, 120, 120);

INSERT INTO item_instances (item_instance_id, item_kind, display_name, visual_id, owner_player_name, state, stack_count)
VALUES (11, 'tool.builder-hammer', 'Builder Hammer', 'tool.builder-hammer', 'Name legacy', 'Inventory', 1),
       (12, 'tool.field-blade', 'Field Blade', 'tool.field-blade', 'Name legacy', 'Inventory', 1),
       (13, 'tool.stone-maul', 'Stone Maul', 'tool.stone-maul', 'Name legacy', 'Inventory', 1),
       (14, 'tool.survey-lantern', 'Survey Lantern', 'tool.survey-lantern', 'Name legacy', 'Inventory', 1);

INSERT INTO player_inventory_slots (player_name, slot_index, item_instance_id, updated_utc)
VALUES ('Name legacy', 0, 11, '2026-01-02T03:04:08.000Z'),
       ('Name legacy', 1, 12, '2026-01-02T03:04:08.000Z'),
       ('Name legacy', 2, 13, '2026-01-02T03:04:08.000Z'),
       ('Name legacy', 3, 14, '2026-01-02T03:04:08.000Z');

INSERT INTO player_starter_grants (player_name, kit_version, updated_utc)
VALUES ('Name legacy', 2, '2026-01-02T03:04:09.000Z');

INSERT INTO world_metadata (key, value) VALUES ('simulation_seed', '5150'), ('world_time_ticks', '50000');
