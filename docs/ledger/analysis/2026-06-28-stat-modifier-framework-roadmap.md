Purpose: Define the remaining roadmap for a future reusable combat stat and modifier framework after the current player-only stat slice.
Status: Analysis
Scope: Generalized stat types, modifiers, and later combat-depth expansion

# Goal

Evolve the current player-only combat stat slice into a reusable modifier-capable framework only when gameplay breadth demands it.

# What Exists Already

The repo now has a practical first pass for player combat:

- `PlayerCombatStats`
- `PlayerCombatDerivedStats`
- attack-speed-driven primary-use cadence
- vitality-driven max HP
- strength-driven damage range
- critical-hit support

# What Is Missing

- reusable stat containers for multiple actor types
- explicit modifier types
- cached stat recomputation rules
- equipment/buff/debuff hooks
- defense/resistance/secondary combat stats

# Recommended Phases

## Phase 1: Shared Combat Stat Value Object

When a second real actor type needs derived stats, introduce a reusable stat core such as:

- `CombatStat`
- `CombatStatType`
- `CombatStatModifier`

Do not do this before another actor truly needs it.

## Phase 2: Multi-Actor Adoption

Adopt the shared stat model for:

- player
- first enemy
- future props only if they actually benefit

Simple destructible props may still be better served by direct health values.

## Phase 3: Modifier Use Cases

Add modifiers only when a real system needs them, such as:

- equipment
- temporary buffs
- debuffs
- status effects

The modifier model should be driven by concrete use cases, not by completeness.

## Phase 4: Broader Combat Depth

Only after the above exists should the project consider:

- defense
- armor or resistance
- attack power abstraction
- attack-range stat
- derived secondary combat values

# Recommended File-Level Focus

- current player stat files:
  - `dream/PlayerCombatStats.cs`
  - `dream/PlayerCombatDerivedStats.cs`
  - `dream/PlayerCombatant.cs`
- future shared stat files only after multi-actor adoption is real

# Risks

- replacing the current simple player stat slice with a speculative framework too early
- forcing all world objects onto a stat matrix they do not need
- creating dead modifier code before there are item or buff systems

# Recommendation

Keep the current player stat slice as the production path until the first enemy or equipment system proves the need for true shared modifiers. This track should remain the most intentionally deferred of the four.
