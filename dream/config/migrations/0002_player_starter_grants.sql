-- The starter kit version each player has received (see ToolCatalog.StarterKit). The starter
-- top-up grants only the kinds introduced after the recorded version, so an item a player gets rid
-- of is never granted again. Owned by the inventory repository (SqliteInventoryRepository).
-- No backfill: a player with inventory rows but no grant row is taken to hold kit v1 (the pre-T2
-- kit) and gets a row the next time its inventory is ensured.

CREATE TABLE IF NOT EXISTS player_starter_grants (
    player_name TEXT NOT NULL PRIMARY KEY,
    kit_version INTEGER NOT NULL,
    updated_utc TEXT NOT NULL,
    FOREIGN KEY (player_name) REFERENCES player_profiles(player_name) ON DELETE CASCADE,
    CHECK (length(trim(player_name)) > 0),
    CHECK (kit_version >= 0)
);
