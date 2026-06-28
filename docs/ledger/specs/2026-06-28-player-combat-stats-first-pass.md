Purpose: Define the first real player combat stat slice so player damage, attack cadence, critical hits, and vitality-driven health are gameplay values instead of ad hoc scene logic.
Status: Approved in chat, pending implementation.
Scope: Player-owned combat stats, derived damage and cooldown rules, HUD exposure, settings-menu editing, and floating-text crit feedback

# Summary

Combat mode currently resolves damage through a direct random roll in `PlayScene`. This spec replaces that prototype path with a small player-owned stat model that drives damage range, attack cadence, max HP, and critical-hit behavior.

The goal is to land a real combat slice without jumping into a full shared modifier engine yet. The first pass is intentionally player-centric, but it should be shaped so later enemies or equipment can reuse the same ideas.

# Goals

- add editable player combat stats for `Strength`, `AttackSpeed`, and `Vitality`
- derive combat behavior from those stats instead of using an ad hoc damage roll in `PlayScene`
- make `AttackSpeed` affect actual combat cadence immediately
- make `Vitality` determine player max HP
- show the current player damage range in the existing top-left HUD
- show critical hits as red floating text
- keep gameplay-state ownership outside `Render` and `GameSettings`

# Non-Goals

- a full generic stat/modifier framework for every combat actor
- equipment, buffs, debuffs, or status effects
- defense, armor, dodge, resistance, or range stats
- enemy AI or a broader hostile-combat loop
- persistent save/load for combat stats

# Design

## Ownership

`PlayerCombatant` owns the player's combat stats because they directly affect player health and outgoing combat behavior.

`PlayScene` remains responsible for scene orchestration:

- resolving what world object was hit
- deciding when a combat primary-use attempt happens
- publishing combat events after a resolved hit

`Render` and `HudRenderer` remain display-only. They receive a status snapshot for current values and emit a request object when the user edits stats in the menu.

`GameSettings` does not become the owner of strength, vitality, or attack speed. Those are gameplay values, not runtime shell settings.

## Stat Model

The first pass introduces a small mutable player stat holder with exactly three raw values:

- `Strength`
- `AttackSpeed`
- `Vitality`

Those raw values produce a derived combat snapshot that is safe to display and safe to use during attack resolution.

Recommended defaults:

- `Strength = 10`
- `AttackSpeed = 2.0`
- `Vitality = 10`

Recommended menu ranges:

- `Strength`: `1` to `50`
- `AttackSpeed`: `0.5` to `5.0`
- `Vitality`: `1` to `50`

## Derived Rules

The first pass uses a compact deterministic ruleset:

- `MinDamage = 5 + (Strength * 2)`
- `MaxDamage = 10 + (Strength * 4)`
- `CritChance = 0.15`
- `CritDamage = round(MaxDamage * 1.5)`
- `AttackIntervalSeconds = 1.0 / AttackSpeed`
- `MaxHealth = 50 + (Vitality * 5)`

These formulas are intentionally simple:

- they keep default damage meaningful against current destructible cubes
- they keep vitality aligned with the current `100 HP` default at `Vitality = 10`
- they make attack speed understandable as attacks per second

## Health Recalculation

When vitality changes at runtime, player max health is recalculated immediately.

Current health should preserve its ratio instead of hard-resetting to full or leaving the old absolute value untouched.

Example:

- player is at `40 / 100`
- vitality increases and max health becomes `150`
- current health becomes `60 / 150`

This keeps live menu tuning stable during testing and avoids accidental healing or damage spikes from stat edits.

## Combat Resolution

Combat mode keeps the existing world-hit resolver for now.

When the player triggers a combat primary use:

1. `PlayScene` asks `PlayerCombatant` whether an attack is ready based on attack cooldown state.
2. If not ready, the action ends without applying damage.
3. If ready, `PlayScene` resolves the hit target through the existing world-object path.
4. If a target exists, `PlayerCombatant` rolls the outgoing attack result from the current derived combat snapshot.
5. `PlayScene` applies that damage to the hit target.
6. `PlayScene` publishes `DamageAppliedEvent` with either:
   - `FloatingCombatTextKind.Regular`
   - `FloatingCombatTextKind.Critical`

This keeps target resolution scene-owned while moving combat math into player-owned combat code.

## Critical Hits

Critical hits use:

- `15%` chance
- red floating text
- exact damage of `1.5 * MaxDamage`, rounded to the nearest integer

Critical hits do not roll within the normal min/max range in this first pass. They intentionally feel like a clear spike.

## HUD And Settings Menu

The existing top-left HUD stays the single gameplay status panel.

It should show:

- HP
- active interaction mode
- damage range in `min-max` form

The existing settings window gains three controls:

- `Strength`
- `Attack Speed`
- `Vitality`

Editing those controls should update live gameplay values through a HUD request -> scene apply path, not through direct render-side mutation.

## Testing

Implementation should add or update focused tests for:

- derived damage range from strength
- derived max HP from vitality
- preserved health ratio when vitality changes
- attack-speed cooldown gating
- critical-hit damage and critical floating-text kind
- HUD snapshot formatting for damage range

# Implementation Notes

- keep the stat slice player-focused for now instead of introducing a shared modifier engine
- prefer small focused types such as a stat holder, derived snapshot, and attack result
- keep `PlayScene` thinner after the change, not thicker
- avoid pushing gameplay numbers into `GameSettings`
