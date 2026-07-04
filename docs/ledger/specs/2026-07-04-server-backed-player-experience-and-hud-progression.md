Purpose: Define the approved server-authoritative experience, leveling, and HUD progression slice.
Status: Approved
Scope: Player profile persistence, progression formulas, enemy kill rewards, and HUD progression display.

## Goals

- Add persistent player experience to the server-owned player profile table.
- Keep level-threshold and enemy XP reward balance in the DB-backed formula system.
- Award XP only on the authoritative server when an enemy dies from player combat.
- Show progression in the gameplay HUD as level, percent-to-next-level, and raw XP progress.

## Non-Goals

- skill trees
- stat auto-allocation on level-up
- item or cube XP rewards
- party/shared XP

## Persistence

- `player_profiles` gains an `experience` integer column.
- `PersistedCreatureStats` gains `Experience`.
- Player reconnect restore uses persisted `Level` and `Experience`.

## Formula Keys

Add DB-backed progression formulas:

- `player.experience_to_next_level`
- `enemy.experience_reward`

The server evaluates them with a strict variable whitelist.

### Player Progression Variables

- `level`
- `experience`

### Enemy Reward Variables

- `enemy_level`
- `player_level`

## Runtime Rules

- XP is granted only when an enemy kill is authoritative and fatal.
- The killer is the player session that caused the fatal combat hit.
- After XP grant, the server:
  - adds reward XP
  - resolves one or more level-ups if the threshold is crossed
  - persists updated `Level` and `Experience`
  - recomputes player-derived combat stats using the existing formula service

## HUD

The top-left HUD now shows:

- `LVL: <level>`
- `EXPERIENCE: <percent>%`
- `<current>/<required>`

The HUD reflects authoritative progression state exported through `WorldSessionPresentationState`.

## Default Formula Intent

The default seeded formulas should provide a working first-pass progression curve and reward curve, while remaining easy to rebalance by editing the DB.

## Test Expectations

- player profile persistence round-trips `Experience`
- formula service evaluates progression threshold and enemy reward formulas
- fatal enemy hits award XP to the correct player
- level-up logic supports crossing more than one threshold in one grant
- HUD snapshot formats level, percent, and raw progress correctly
