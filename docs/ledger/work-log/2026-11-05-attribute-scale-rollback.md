# Attribute scale rollback: the origin moves back to 10

Continues `2026-11-05-master-green-attribute-ceiling-fallout.md`, which left the 100x scale move as a balance decision reserved for the user. It is now decided.

## Decision

`PlayerStatRules.NewCharacterAttribute` goes from 1000 back to **10**. The 10000 `PlayerCombatStats.AttributeCeiling` is kept deliberately as headroom for gear and levels. Nothing else in code changes: `Start*` already derive from the constant, and `PersistedPlayerProfile.CreateDefault` derives its health seed from it, so both follow.

## Rejected: rescale every coefficient by 1/100

It would have fixed combat (`min_damage`, `max_health`, `Defense`, attack speed) and left itemisation broken. The attribute bonuses in `dream/config/content/items.json` are small: Strength flats are `+1/+2/+4/+8`, and the largest rolled maxima across all items are Strength 19, Armor 19, Energy 14, Agility 12, Vitality 9. At origin 10 a `+8` ring is a real upgrade; at origin 1000 it is a 0.8% rounding error. The 100x origin broke itemisation as well as combat, so only moving the origin fixes both.

## Migration: spend is preserved

`dream/config/migrations/0015_attribute_origin.sql` subtracts 990 from each stored attribute that is `>= 1000`, but only on rows with `created_utc >= '2026-11-04T19:44:37Z'` (the author time of `a39702e6`, 21:44:37 +02:00, which made new characters start at 1000). Points are derived, not stored: `PointsSpent` is how far attributes sit above the origin. Since `990 = 1000 - 10`, a stored `1000 + n` becomes `10 + n` against an origin that also moved from 1000 to 10, so `PointsSpent` is bit-identical for migrated characters. A stored 2000 becomes 1010, which is 1000 above the new origin just as 2000 was 1000 above the old one.

### Why creation time gates it

Not the attribute values, and not level. Before `1f422512` the attribute ceiling was 1000 and the property setters clamp, so every over-cap write stored exactly 1000; before `751c9fd9` there was no point budget at all. A character created before `a39702e6` could therefore sit at 1000 having spent nothing from the 10 origin, and attributes plus level cannot tell it from a character created at the 1000 origin (point-budget arithmetic was tried in both forms and fails: it either leaves the legacy row unprotected or makes nothing migrate). Only characters created after the 1000 origin landed are migrated.

Erring late is deliberate. A spared 1000-origin character keeps its stats but reads as about 3960 points overspent, so its stats are unspendable until an operator fixes them: bad but reversible. A wrongly migrated legacy character has its investment destroyed in place, recoverable only from the automatic `.v0014-to-v0015` backup. Unspendable-but-intact beats silently destroyed. Migrations are versioned and run once per database, so the script is not written to be idempotent.

## Accepted consequence

A migrated character arrives at full health once. The join recomputes max health from the formula (`50 + vitality * 5`, now far smaller), and `HealthState.SetAuthoritativeState` clamps stored current health to it, so a character stored below max is lifted to the new max on first join. This one-off heal is precedented by migration `0013`'s mana.

## Also recorded

The stale claim that the `player_profiles` column defaults were the `Start*` values was wrong: `0001_baseline.sql` defaults attributes to 10 and hp to 100, and no later migration changed them. `UnseededAttribute` (10) now equals `PlayerStatRules.Start*`, so an unseeded sheet and a new character agree for the first time.
