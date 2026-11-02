-- Migration 0013: quickbars and real mana (M4). Until now the runtime wrote current_energy = 0 on
-- every save, so no stored value means anything. Every row is set high and the join clamps it to the
-- character's maximum, which leaves everybody at full mana once.
UPDATE player_world_state SET current_energy = 1000000;

CREATE TABLE player_quickbar_slots (
  world_id INTEGER NOT NULL,
  player_name TEXT NOT NULL,
  slot INTEGER NOT NULL,
  kind INTEGER NOT NULL,
  ref_key TEXT NOT NULL DEFAULT '',
  updated_utc TEXT NOT NULL,
  PRIMARY KEY (world_id, player_name, slot),
  FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
  FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
  CHECK (length(trim(player_name)) > 0),
  CHECK (slot BETWEEN 0 AND 5),
  CHECK (kind BETWEEN 0 AND 2));
