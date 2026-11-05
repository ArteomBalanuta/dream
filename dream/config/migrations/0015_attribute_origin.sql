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
-- Which rows move is decided by creation time, not by the attribute values. Before commit 1f422512 the
-- attribute ceiling (PlayerCombatStats.MaxStrength/MaxVitality/MaxAgility/MaxEnergy) was 1000 and the
-- property setters clamp, so a character created before a39702e6 -- when the schema default and the
-- origin were both 10, and before the point budget of 751c9fd9 existed at all -- could sit at exactly
-- 1000 without a single point of the new origin behind it: every over-cap free-slider write was stored
-- as 1000. Attributes and level cannot tell that character from one created at the 1000 origin, so
-- neither a ">= 1000" test nor any point-budget arithmetic can separate them. created_utc can: a row
-- created before a39702e6 (2026-11-04T19:44:37Z, committed 21:44:37 +02:00) cannot have been created at
-- 1000, and is left exactly as stored.
--
-- created_utc is TEXT, compared lexicographically (BINARY collation). Rows written by the server are in
-- SqliteSchemaBootstrapper.FormatUtc's round-trip "O" form ('2026-11-04T19:44:37.1234567Z', 7 fractional
-- digits); rows that took the column default are in strftime('%Y-%m-%dT%H:%M:%fZ') form
-- ('2026-11-04T19:44:37.123Z'). Both share the YYYY-MM-DDTHH:MM:SS prefix. The cutoff literal below is
-- that prefix with no trailing 'Z': byte 19 of a fractional row is '.' (0x2E) and of a whole-second
-- row 'Z' (0x5A), and either sorts above the shorter literal, so the whole cutoff second migrates and
-- every earlier second is spared. (With a trailing 'Z' the literal would sort ABOVE every fractional
-- row in the cutoff second and spare them.)
--
-- Erring late is deliberate, but it costs on both sides. A 1000-origin character that is wrongly spared
-- (reachable: a server whose clock was behind at creation, since the 1000 origin existed for under 24
-- hours, so any skew beyond that misses the window) keeps 1000 in each attribute. It reads as ~3960
-- points overspent, so its stats are unspendable, AND it is the very bug this migration exists to fix:
-- 5050 hp, 2005-4010 damage, the 5.0 attack-speed cap, effectively unkillable (StatRules.MaxEnemyBase
-- is 50, so the worst enemy bite is ~52), for that character and anyone sharing a world with it, PvP
-- included. An operator can still fix it by hand. A legacy character that is wrongly migrated has ~990
-- points of real investment destroyed in place per clamped attribute, recoverable only from the
-- automatic {db}.v0014-to-v0015.*.bak. That asymmetric half is reachable too: a pre-a39702e6 row whose
-- created_utc was written by a host whose clock ran ahead, or a hand-written dev/fixture row with a
-- far-future stamp, is migrated. Destroying a legacy character's investment is judged worse than
-- leaving a broken-but-intact character for an operator to fix.
--
-- Within a migrated row the per-attribute ">= 1000" CASE is still kept, so an attribute below 1000
-- (a spend-free 10, say) is not driven negative or floored. A stored 2000 becomes 1010: it is 1000
-- above the old origin and 1010 is 1000 above the new one, so the spend is preserved. Migrations are
-- versioned and run once per database, so the script need not be idempotent.
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
WHERE (strength >= 1000 OR agility >= 1000 OR vitality >= 1000 OR energy >= 1000)
  AND created_utc >= '2026-11-04T19:44:37';
