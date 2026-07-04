Purpose: Define the approved server-side abstraction and denormalized persistence design for players, enemies, NPCs, items, inventory, cubes, and active world state.
Status: Proposed
Scope: Authoritative server runtime structure, SQLite schema direction, persistence lifecycle, and restore/save responsibilities.

## Goals

- Keep the server as the single authoritative source for multiplayer state.
- Persist players by name, including mode, stats, and map position.
- Persist surviving player-made cubes with ownership.
- Persist active alive enemies with stats and world position.
- Introduce a clean path for items, inventories, hotbar slots, and dropped pickups.
- Prefer denormalized SQLite tables that are easy to restore into runtime state.

## Non-Goals

- full ECS conversion
- event-sourced persistence
- deeply normalized relational modeling
- buff/debuff/equipment modifier engine in this phase
- full NPC behavior implementation in this phase

## Runtime Architecture

### Shared Principle

Runtime sharing happens through capabilities and snapshots, not a deep inheritance hierarchy.

### Family Boundaries

#### Player family

Runtime types:

- `Player`
- `RemoteSessionPlayerState`

Shared responsibilities:

- authoritative actor identity
- position/facing
- mode
- combat stats and health
- inventory ownership
- equipped/active item selection

#### Enemy family

Runtime types:

- `EnemyActor`
- `EnemyCombatant`

Shared responsibilities:

- authoritative actor identity
- kind-specific AI and movement
- combat stats and health
- position/facing

#### NPC family

Future runtime types:

- `NpcActor`
- optional `NpcCombatant` if needed

NPCs should reuse actor/stat capabilities but remain a separate runtime family from enemies until the behavior overlap is proven.

#### Item family

Runtime types:

- `ItemInstance`
- optional `ItemDefinition`

Shared responsibilities:

- instance identity
- display name
- item kind / definition id
- visual id for client model/viewmodel lookup
- ownership state
- world-drop state

Items are not creatures. They should never inherit from the creature or combat actor path just to share identity.

#### Inventory family

Runtime types:

- `PlayerInventory`
- `InventorySlotAssignment`

Responsibilities:

- player ownership of items
- hotbar slots `1..6`
- equipped/held item resolution

## Shared Contracts

### Shared actor snapshot

All actor families that replicate to clients should expose:

- actor id
- actor kind
- display name
- position
- facing
- alive state
- current/max HP where applicable
- stat snapshot where applicable

### Shared stat snapshot

Introduce one reusable persistence/replication stat snapshot for creature families with:

- current HP
- max HP
- strength
- agility
- vitality
- energy
- movement speed
- attack speed
- level

The runtime can continue to use family-specific wrappers internally where helpful.

## Denormalized SQLite Schema

### `player_profiles`

One row per player name.

Fields:

- `player_name` primary key
- `display_name`
- `interaction_mode`
- `strength`
- `agility`
- `vitality`
- `energy`
- `movement_speed`
- `attack_speed`
- `level`
- timestamps

### `player_world_state`

One row per player name.

Fields:

- `player_name` primary key
- `position_x`, `position_y`, `position_z`
- `facing_x`, `facing_y`, `facing_z`
- `current_hp`
- `current_energy`
- checkpoint timestamps

This duplicates some player data intentionally for easy restore.

### `placed_cube_instances`

One row per surviving placed cube.

Fields:

- `cube_id` primary key
- `owner_player_name`
- `cell_x`, `cell_y`, `cell_z`: coarse grid ownership cell
- `center_x`, `center_y`, `center_z`: nullable exact world-space render/physics center for dynamic pushed cubes
- `material_id`
- `shape_kind`
- optional durability/state fields if needed
- timestamps

### `enemy_instances`

One row per active alive enemy.

Fields:

- `enemy_id` primary key
- `enemy_kind`
- `display_name`
- `level`
- `position_x`, `position_y`, `position_z`
- `facing_x`, `facing_y`, `facing_z`
- `current_hp`
- `max_hp`
- `strength`
- `agility`
- `vitality`
- `energy`
- `movement_speed`
- `attack_speed`
- timestamps

### `npc_instances`

Deferred until the first real NPC runtime family exists.

### `item_instances`

One row per item instance.

Fields:

- `item_instance_id` primary key
- `item_kind`
- `display_name`
- `visual_id`
- `owner_player_name` nullable
- `world_position_x`, `world_position_y`, `world_position_z` nullable
- `state` such as `World`, `Inventory`, `Equipped`
- `stack_count`
- timestamps

This intentionally keeps both ownership and world placement on the same row.

### `player_inventory_slots`

One row per occupied hotbar slot.

Fields:

- `player_name`
- `slot_index`
- `item_instance_id`
- primary key on `(player_name, slot_index)`

## Persistence Lifecycle

### Startup restore

1. Bootstrap schema.
2. Read player profile/world rows.
3. Read cube rows and rebuild surviving placed cubes.
4. Read active enemy rows and rebuild runtime enemies.
5. Read item rows and slot rows to rebuild world drops and inventories.
6. If no persisted world exists, fall back to generated bootstrap world.

### Immediate saves

Persist immediately after:

- accepted mode change
- accepted player stat change
- cube placement
- cube destruction/removal/fracture state change
- item pickup/drop/equip/unequip
- enemy spawn/despawn/death

### Checkpoint saves

Every 5 seconds, persist moving state:

- player position/facing/current HP/current energy
- enemy position/facing/current HP

## Server Authority Rules

- player name is the persistence identity
- reconnect by same player name restores exact saved position
- each player owns their own mode and stats
- server decides item ownership and slot assignment
- server decides which cubes and enemies survive
- clients request state changes; the server accepts or rejects and then persists

## Replication Direction

Replication should align with persistence boundaries:

- per-actor stats and mode come from the actor snapshot, not a single global mode
- item ownership/equipped state should be replicable per player
- world-dropped items should replicate as world item instances
- enemy stats and HP replicate from persistent enemy instance state

## Implementation Strategy

Implement in phases:

1. player profile/world state repository
2. enemy persistence repository
3. placed cube persistence repository
4. item/inventory runtime model plus persistence
5. snapshot and protocol expansion for item/equipped state

## Success Criteria

- two players can reconnect by name and restore distinct server-owned stats/mode/position
- player-made cubes survive restart with owner name preserved
- active wolves survive restart with HP/name/level/position preserved
- server can represent item ownership, equipped items, and world drops
- runtime abstractions remain family-based and extensible without a large inheritance tree
