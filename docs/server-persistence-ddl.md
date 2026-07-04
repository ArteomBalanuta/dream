## Purpose

Document the authoritative SQLite schema that backs player profiles, world state, enemies, placed cubes, items, and inventory.

## Source Of Truth

The executable bootstrap script lives at [dream/config/player-profiles.init.sql](/G:/workspace/projects/dream/dream/config/player-profiles.init.sql).

This document explains the intent behind each table so future runtime and migration work stays aligned with the current denormalized design.

## Design Notes

- The schema is intentionally denormalized.
- Restore speed and straightforward authoritative writes matter more than strict normalization.
- Player identity is currently keyed by `player_name`.
- Inventory ownership uses item rows plus slot-assignment rows.
- The server remains the only authority that should write gameplay state into these tables.
- Authoritative player combat formulas are now persisted as editable expression strings in SQLite.

## Tables

### `player_profiles`

One row per persistent player identity.

Key fields:

- `player_name`: primary key
- `display_name`: current multiplayer-visible name
- `interaction_mode`: `Building` or `Combat`
- combat stat columns: `strength`, `agility`, `vitality`, `energy`
- movement/combat tempo: `movement_speed`, `attack_speed`
- progression and health: `level`, `current_hp`, `max_hp`
- progression tracking: `level`, `experience`, `current_hp`, `max_hp`
- timestamps: `created_utc`, `updated_utc`

Used for:

- creating or restoring a named player profile at join time
- persisting authoritative base stat and mode changes
- reconnect restore of long-lived character state
- reconnect restore of long-lived character progression state

Important note:

- effective combat stats from equipped tools are not stored here
- they are recomputed on the server from base stats plus equipped inventory state
- current XP is stored here, while required next-level XP is computed from server formulas

### `player_world_state`

One row per player position/facing checkpoint.

Key fields:

- `player_name`: primary key and FK to `player_profiles`
- `position_x`, `position_y`, `position_z`
- `facing_x`, `facing_y`, `facing_z`
- `current_hp`
- `current_energy`
- `updated_utc`

Used for:

- reconnect restore of map position and facing
- periodic 5-second authoritative checkpoints

### `placed_cube_instances`

One row per surviving player-made cube.

Key fields:

- `cube_id`: primary key
- `owner_player_name`: FK to `player_profiles`
- `cell_x`, `cell_y`, `cell_z`
- `shape_kind`
- `material_id`
- `current_health`, `max_health`
- timestamps

Used for:

- startup world restore
- cube placement persistence
- cube destruction and fracture cleanup

### `enemy_instances`

One row per currently active enemy instance.

Key fields:

- `enemy_id`: primary key
- `enemy_kind`
- `display_name`
- `position_*`, `facing_*`
- `current_hp`, `max_hp`
- creature stats and tempo columns
- `level`
- timestamps

Used for:

- authoritative enemy startup restore
- HP/position/facing checkpoints
- despawn and death cleanup

### `item_instances`

One row per persistent item instance.

Key fields:

- `item_instance_id`: primary key
- `item_kind`
- `display_name`
- `visual_id`
- `owner_player_name`: nullable FK to `player_profiles`
- `world_position_*`: nullable world-drop coordinates
- `state`: current ownership/state marker
- `stack_count`
- timestamps

Used for:

- player inventory contents
- future world drops and pickups
- held/equipped-item visual resolution through `visual_id`

### `player_inventory_slots`

One row per occupied player inventory or equipped slot.

Key fields:

- composite PK: `player_name`, `slot_index`
- `item_instance_id`
- `updated_utc`

Used for:

- 4x4 backpack slot assignment
- dedicated equipped-slot assignment
- reconnect restore of tool selection

### `combat_formulas`

One row per authoritative combat formula expression.

Key fields:

- `formula_key`: stable formula id such as `player.max_health`
- `expression_text`: arithmetic expression string used by the authoritative server
- `description`: plain-language note for the formula
- `enabled`: active/inactive toggle
- `updated_utc`

Used for:

- authoritative player stat derivation
- rebalance of HP, damage, crit, and attack cadence without recompiling
- rebalance of XP thresholds and enemy XP rewards without recompiling
- server startup validation of required combat-rule rows

Important note:

- the client does not authoritatively evaluate these expressions
- the server loads, validates, and evaluates them through a strict whitelisted arithmetic engine

## Indexes

The current script adds lookup indexes for:

- recently updated player profiles
- recently updated player world state
- cube ownership and update time
- enemy update time
- item ownership and update time
- slot lookup by `item_instance_id`
- player profile change history by player and time

These indexes are aimed at:

- fast startup restore scans
- direct player-name lookups
- efficient authoritative checkpoint writes

## Triggers

`trg_player_profiles_set_updated_utc` keeps `player_profiles.updated_utc` fresh on update when callers do not explicitly change the timestamp value.

## Current Inventory Convention

The gameplay inventory slice uses:

- backpack slots `0..15`
- one equipped slot stored separately
- item definitions resolved in code by `item_kind`
- per-item held-viewmodel selection resolved by `visual_id`

## Migration Guidance

When extending this schema:

- prefer additive columns and additive tables
- keep startup restore queries direct
- avoid collapsing players, enemies, cubes, and items into one generic entity table
- keep formula evaluation server-owned even when the source strings live in the DB
- document new runtime assumptions in the ledger and here together
