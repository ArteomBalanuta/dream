-- Migration 0012: ground items and wallets (M3). An item instance is referenced by at most one slot
-- row, in the whole world: the unique index below is the backstop against a duplicated item. Rows
-- written before it could share an item (a hand edit, an old swap bug), so the extra references go
-- first. The keeper is deterministic: the owner's own row wins, then a worn row over a bag row over
-- a held-back row, then the lowest player name and slot. The other players lose the row, never the
-- item. Ground items are item_instances rows in state 'Ground' that no slot references; the partial
-- index serves their load. player_wallets holds one gold balance per player and world, cleared with
-- the player (profile) or the world.
DELETE FROM player_inventory_slots WHERE rowid IN (
  SELECT id FROM (
    SELECT s.rowid AS id, ROW_NUMBER() OVER (
      PARTITION BY s.world_id, s.item_instance_id
      ORDER BY CASE WHEN i.owner_player_name = s.player_name THEN 0 ELSE 1 END,
               CASE WHEN s.slot_index BETWEEN 100 AND 111 THEN 0
                    WHEN s.slot_index BETWEEN 0 AND 63 THEN 1
                    WHEN s.slot_index BETWEEN 200 AND 215 THEN 2 ELSE 3 END,
               s.player_name, s.slot_index) AS n
    FROM player_inventory_slots AS s
    LEFT JOIN item_instances AS i
      ON i.world_id = s.world_id AND i.item_instance_id = s.item_instance_id)
  WHERE n > 1);

DROP INDEX IF EXISTS ix_player_inventory_slots_item_instance_id;

CREATE UNIQUE INDEX ux_player_inventory_slots_item
  ON player_inventory_slots (world_id, item_instance_id);

CREATE INDEX ix_item_instances_ground ON item_instances (world_id) WHERE state = 'Ground';

CREATE TABLE player_wallets (
  world_id INTEGER NOT NULL,
  player_name TEXT NOT NULL,
  gold INTEGER NOT NULL DEFAULT 0,
  updated_utc TEXT NOT NULL,
  PRIMARY KEY (world_id, player_name),
  FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
  FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
  CHECK (length(trim(player_name)) > 0),
  CHECK (gold >= 0));
