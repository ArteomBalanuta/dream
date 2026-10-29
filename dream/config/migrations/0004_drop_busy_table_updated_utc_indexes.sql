-- Migration 0004: drop the updated_utc indexes of the three busy tables.
-- Every checkpoint UPDATE of placed_cube_instances, cube_fracture_pieces and enemy_instances sets
-- updated_utc, so each of those rows pays an index delete and insert for an index no query reads
-- (nothing filters or orders by updated_utc). Measured with a 10000-row UPDATE loop, the index was
-- about 25% of the statement's time; with migration 0003's composite (world_id, id) keys it made the
-- World10k checkpoint write about 12% slower than before 0003. The other updated_utc indexes
-- (player_profiles, player_world_state, item_instances) are not on a hot write path and stay.
-- DROP INDEX only: no table is rebuilt and no row changes.

DROP INDEX IF EXISTS ix_placed_cube_instances_updated_utc;
DROP INDEX IF EXISTS ix_cube_fracture_pieces_updated_utc;
DROP INDEX IF EXISTS ix_enemy_instances_updated_utc;
