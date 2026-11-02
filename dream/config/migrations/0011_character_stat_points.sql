-- Migration 0011: unspent attribute points (character stats sheet). A character earns 5 points per
-- level above 1; existing characters are credited what their level has earned so far. Strength,
-- agility, vitality and energy keep the values the row holds (the columns exist since the baseline).
ALTER TABLE player_profiles ADD COLUMN stat_points INTEGER NOT NULL DEFAULT 0 CHECK (stat_points >= 0);

-- The backfill must not move updated_utc (the checkpoint guard compares it), and trg_player_profiles_set_updated_utc
-- stamps it on every update. So the trigger is dropped around the backfill and created again as the baseline wrote it.
DROP TRIGGER IF EXISTS trg_player_profiles_set_updated_utc;

UPDATE player_profiles SET stat_points = (level - 1) * 5;

CREATE TRIGGER IF NOT EXISTS trg_player_profiles_set_updated_utc
AFTER UPDATE ON player_profiles
FOR EACH ROW
WHEN NEW.updated_utc = OLD.updated_utc
BEGIN
    UPDATE player_profiles
    SET updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE player_name = NEW.player_name;
END;
