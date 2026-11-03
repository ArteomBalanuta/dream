-- Migration 0014: quests, account roles and sanctions, character presence (M6, M8).
ALTER TABLE accounts ADD COLUMN role INTEGER NOT NULL DEFAULT 0 CHECK (role IN (0, 1));
ALTER TABLE accounts ADD COLUMN banned_until_utc TEXT NULL;
ALTER TABLE accounts ADD COLUMN ban_reason TEXT NULL;
ALTER TABLE accounts ADD COLUMN muted_until_utc TEXT NULL;

CREATE TABLE player_quests (
  world_id INTEGER NOT NULL,
  player_name TEXT NOT NULL,
  quest_key TEXT NOT NULL,
  state INTEGER NOT NULL,
  progress TEXT NOT NULL DEFAULT '',
  updated_utc TEXT NOT NULL,
  PRIMARY KEY (world_id, player_name, quest_key),
  FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
  FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
  CHECK (length(trim(player_name)) > 0),
  CHECK (length(trim(quest_key)) > 0),
  CHECK (state IN (1, 2)));

-- No foreign key to player_profiles: names are immutable keys and a row expires by itself.
CREATE TABLE character_presence (
  player_name TEXT NOT NULL PRIMARY KEY,
  world_id INTEGER NOT NULL REFERENCES worlds(world_id) ON DELETE CASCADE,
  expires_utc TEXT NOT NULL,
  CHECK (length(trim(player_name)) > 0));
