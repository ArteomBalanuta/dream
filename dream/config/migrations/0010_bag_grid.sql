-- Migration 0010: the footprint bag (classic HUD iteration 2). Data only: no table is rebuilt, so the
-- foreign keys, the indexes and trg_item_instances_advance_object_id stay as they are, and no item
-- row is touched. player_inventory_slots.slot_index changes meaning:
--   before: 0..15 a 4x4 backpack slot, 100..103 an equipment slot that normally also had a backpack
--           row (the alias)
--   after:  0..63 the anchor (row * 8 + col) of an item on the 8x8 bag board, 100..111 the equipment
--           slots (no alias: a worn item has only its worn row), 200..215 held-back items
-- The server repairs what a row layout cannot say on its own (overlaps, items that no longer fit)
-- when it loads an inventory; this script only has to make every row land on a legal, distinct index.
--
-- The steps run in this order, and the order matters.

-- 1. Hand-edited rows in 16..99 would become legal bag anchors and could collide with the remapped
--    rows below. Park them as unknown rows (kept and inert, as any other index outside the three
--    ranges is). The shifted range, 20016..20099, is never written by any build.
UPDATE player_inventory_slots SET slot_index = slot_index + 20000 WHERE slot_index BETWEEN 16 AND 99;

-- 2. A worn item no longer keeps a bag row. Rows 100..111 are worn slots in the new layout (a
--    hand-edited 104..111 row of the old layout is one too), so the bag row of the same player that
--    holds the same item goes. The worn row stays.
DELETE FROM player_inventory_slots
WHERE slot_index BETWEEN 0 AND 15 AND EXISTS (
    SELECT 1 FROM player_inventory_slots AS e
    WHERE e.world_id = player_inventory_slots.world_id
      AND e.player_name = player_inventory_slots.player_name
      AND e.item_instance_id = player_inventory_slots.item_instance_id
      AND e.slot_index BETWEEN 100 AND 111);

-- 3. Old slot i (row i / 4, column i % 4 of the 4x4 backpack) becomes the anchor of a 2x2 block of
--    the 8x8 board: row (i / 4) * 2, column (i % 4) * 2, that is (i / 4) * 16 + (i % 4) * 2. Anything
--    up to 2x2 then fits where it was; larger items (a 2x3 blade) can overlap the block below and are
--    re-homed by the server's load-time repair, or held back when the board is full. Two steps,
--    because slot_index is part of the primary key and the old and new ranges overlap: first move
--    the rows out of the way (10000..10015), then to their anchors.
UPDATE player_inventory_slots SET slot_index = slot_index + 10000 WHERE slot_index BETWEEN 0 AND 15;
UPDATE player_inventory_slots
SET slot_index = ((slot_index - 10000) / 4) * 16 + ((slot_index - 10000) % 4) * 2
WHERE slot_index BETWEEN 10000 AND 10015;
