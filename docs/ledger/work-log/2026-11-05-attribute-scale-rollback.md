# Attribute scale rollback: the origin moves back to 10

Continues `2026-11-05-master-green-attribute-ceiling-fallout.md`, which left the 100x scale move as a balance decision reserved for the user. It is now decided.

## Decision

`PlayerStatRules.NewCharacterAttribute` goes from 1000 back to **10**. The 10000 `PlayerCombatStats.AttributeCeiling` is kept deliberately as headroom for gear and levels. Nothing else in code changes: `Start*` already derive from the constant, and `PersistedPlayerProfile.CreateDefault` derives its health seed from it, so both follow.

## Rejected: rescale every coefficient by 1/100

It would have fixed combat (`min_damage`, `max_health`, `Defense`, attack speed) and left itemisation broken. Every gear attribute bonus in `dream/config/content/items.json` is single-digit (`+1/+2/+4/+8` Strength flats; rolled Strength 4-9, Vitality 0-6, Armor 1-11). At origin 10 a `+8` ring is a real upgrade; at origin 1000 it is a 0.8% rounding error. The 100x origin broke itemisation as well as combat, so only moving the origin fixes both.

## Migration: spend is preserved

`dream/config/migrations/0015_attribute_origin.sql` subtracts 990 from each stored attribute. Points are derived, not stored: `PointsSpent` is how far attributes sit above the origin. Since `990 = 1000 - 10`, a stored `1000 + n` becomes `10 + n` against an origin that also moved from 1000 to 10, so `PointsSpent` is bit-identical for migrated characters.

## Accepted consequence

A migrated character arrives at full health once. The join recomputes max health from the formula (`50 + vitality * 5`, now far smaller), and `HealthState.SetAuthoritativeState` clamps stored current health to it, so a character stored below max is lifted to the new max on first join. This one-off heal is precedented by migration `0013`'s mana.

## Also recorded

The stale claim that the `player_profiles` column defaults were the `Start*` values was wrong: `0001_baseline.sql` defaults attributes to 10 and hp to 100, and no later migration changed them. `UnseededAttribute` (10) now equals `PlayerStatRules.Start*`, so an unseeded sheet and a new character agree for the first time.
