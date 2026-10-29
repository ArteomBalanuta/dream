-- Rows of a schema version 4 database that has been played on, applied after migration 0004.
-- World 1: two players; three cubes (the cube 2003 shares its legacy id with the enemy 2003); two
-- rubble groups (source cubes 1002 and 1007) with three pieces; two enemies; items 40, 41 and 42
-- with their slots. World 2: one cube with the same legacy id as one in world 1.
-- Expected ids after 0005 (world 1): base = 43, then cubes 1001 -> 43, 1005 -> 44, 2003 -> 45;
-- rubble groups 1002 -> 46, 1007 -> 47; pieces (1002,0) -> 48, (1007,0) -> 49, (1007,1) -> 50;
-- enemies 2001 -> 51, 2003 -> 52; next_object_id = 53. World 2: cube 1001 -> 1, next_object_id = 2.

INSERT INTO player_profiles (player_name, display_name) VALUES ('alice', 'Alice'), ('bob', 'Bob');

INSERT INTO worlds (world_id, name, width, height, depth, map_source, generator_key, generator_version, created_schema_version)
VALUES (2, 'second', 56, 8, 56, 'generated', 'default', 1, 4);

INSERT INTO placed_cube_instances (world_id, cube_id, owner_player_name, cell_x, cell_y, cell_z, material_id, current_health, max_health)
VALUES (1, 1005, 'alice', 5, 1, 5, 'Stone', 5, 5),
       (1, 1001, 'alice', 1, 1, 1, 'Dirt', 4, 5),
       (1, 2003, 'bob', 3, 1, 3, 'Stone', 5, 5),
       (2, 1001, 'bob', 9, 1, 9, 'Grass', 5, 5);

INSERT INTO cube_fracture_pieces (world_id, source_cube_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z, center_x, center_y, center_z, side_length, material_id)
VALUES (1, 1007, 1, 'alice', 7, 1, 7, 7.75, 1.1, 7.25, 0.5, 'Stone'),
       (1, 1007, 0, 'alice', 7, 1, 7, 7.25, 1.1, 7.25, 0.5, 'Stone'),
       (1, 1002, 0, 'bob', 2, 1, 2, 2.25, 1.1, 2.25, 0.5, 'Dirt');

INSERT INTO enemy_instances (world_id, enemy_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp)
VALUES (1, 2003, 'wolf', 'Wolf', 20, 1, 20, 0, 0, 1, 30, 30),
       (1, 2001, 'wolf', 'Wolf', 22, 1, 22, 0, 0, 1, 25, 30);

INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, owner_player_name, state)
VALUES (1, 40, 'tool.pick', 'Pick', 'pick', 'alice', 'Inventory'),
       (1, 41, 'tool.torch', 'Torch', 'torch', 'alice', 'Inventory'),
       (1, 42, 'tool.pick', 'Pick', 'pick', 'bob', 'Inventory');

INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id, updated_utc)
VALUES (1, 'alice', 0, 40, '2026-01-01T00:00:00.000Z'),
       (1, 'alice', 1, 41, '2026-01-01T00:00:00.000Z'),
       (1, 'bob', 0, 42, '2026-01-01T00:00:00.000Z');
