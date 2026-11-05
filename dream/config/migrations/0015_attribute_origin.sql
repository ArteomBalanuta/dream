-- Move stored characters from the 1000 attribute origin onto the 10 origin.
--
-- Commit a39702e6 raised PlayerStatRules.NewCharacterAttribute to 1000, so characters created since
-- then hold 1000 in each of strength/agility/vitality/energy. The origin is back at 10, and every
-- coefficient in the game is tuned for attributes near 10. PointsSpent sums max(0, attr - Start*), so
-- a stored 1000 read against the 10 origin would count ~990 points per attribute as spent, pin
-- PointsAvailable at 0 and make the character's stats permanently unspendable.
--
-- 990 = 1000 - 10, so 1000 + n becomes 10 + n and (attr - Start) is bit-identical across the
-- migration: every genuine spend is preserved exactly. That identity is the whole point of this script.
--
-- The >= 1000 guard protects characters stored before a39702e6. Subtracting unconditionally and
-- flooring at 10 would delete a legacy character's spends (a stored 14 would become 10 and lose 4
-- earned points). A character that genuinely spent its way to 1000 under the old 10 origin would have
-- needed 990 points, i.e. level 199, which no stored character has.
--
-- Deliberately not done:
--   * No health backfill. player.max_health (50 + vitality * 5) lives in the combat_formulas table and
--     is editable content, so it is not hardcoded here. The load path recomputes max health from the
--     formula and HealthState.SetAuthoritativeState clamps the stored current health to it, so a
--     migrated character loads at full health once -- a one-off heal, as migration 0013 did for mana.
--   * No player_profile_change_log backfill. It is an append-only audit of past edits; rewriting
--     history there would be a lie.
--
-- player_profiles is global rather than per-world (0003_worlds.sql; SqliteWorldCatalog.CopyTables does
-- not copy it), so it is the only table holding player attributes. enemy_instances.strength is an
-- enemy stat and is not touched.
--
-- trg_player_profiles_set_updated_utc fires on this UPDATE, so touched rows get a fresh updated_utc.
UPDATE player_profiles
SET strength = CASE WHEN strength >= 1000 THEN strength - 990 ELSE strength END,
    agility  = CASE WHEN agility  >= 1000 THEN agility  - 990 ELSE agility  END,
    vitality = CASE WHEN vitality >= 1000 THEN vitality - 990 ELSE vitality END,
    energy   = CASE WHEN energy   >= 1000 THEN energy   - 990 ELSE energy   END
WHERE strength >= 1000 OR agility >= 1000 OR vitality >= 1000 OR energy >= 1000;
