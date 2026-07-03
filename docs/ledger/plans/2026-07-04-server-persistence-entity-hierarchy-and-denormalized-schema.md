Purpose: Break the server-side entity hierarchy and denormalized persistence design into execution-ready phases.
Status: In Progress
Scope: SQLite schema growth, runtime abstraction seams, repositories, restore/checkpoint flow, inventory ownership, and replication alignment.

Implementation note:

- Phase 1 through Phase 5 are now implemented in the codebase as the first usable server-bound persistence slice.
- Phase 6 and Phase 7 currently have schema/repository foundations, but full runtime item/inventory behavior and replication are still pending.

## Goal

Deliver a server-first persistence foundation for:

- players by name
- player world state
- player-owned cubes
- active enemies
- item ownership and world drops
- hotbar/inventory slot state

without introducing a deep entity inheritance tree or an over-normalized database.

## Phase 1: Shared Persistence Contracts

1. Introduce persistence DTOs:
   - `PersistedPlayerProfile`
   - `PersistedPlayerWorldState`
   - `PersistedEnemyInstance`
   - `PersistedPlacedCube`
   - `PersistedItemInstance`
   - `PersistedInventorySlot`
2. Introduce repository interfaces:
   - `IPlayerProfileRepository`
   - `IWorldPersistenceRepository`
3. Introduce one shared creature stat snapshot/value object for persistence and replication use.

Exit criteria:

- the server has explicit persistence-facing contracts independent of scene/render code

## Phase 2: Schema Bootstrap Expansion

1. Expand the SQL bootstrap file(s) under `dream/config/`.
2. Add denormalized tables for:
   - player profiles
   - player world state
   - placed cubes
   - enemy instances
   - item instances
   - player inventory slots
3. Add indexes aligned to startup restore and player-name lookups.

Exit criteria:

- schema fully covers the first persistence slice

## Phase 3: Player Persistence

1. Load/create player profile by player name at join time.
2. Restore authoritative mode/stats from the repository.
3. Restore exact persisted player position on reconnect.
4. Persist accepted mode/stat changes immediately.
5. Include player checkpoint persistence every 5 seconds.

Exit criteria:

- joined players no longer leak host mode or stats across sessions
- reconnect restores player position and authoritative stats

## Phase 4: Enemy Persistence

1. Add repository load/save for active alive enemy instances.
2. Restore enemies on server startup before fallback bootstrap generation.
3. Persist enemy spawn/death immediately.
4. Persist enemy position/facing/HP in the 5-second checkpoint.

Exit criteria:

- active wolves survive restart with their persisted HP/name/level/position

## Phase 5: Placed Cube Persistence

1. Persist surviving placed cubes with owner player name and shape/material state.
2. Restore cubes into the authoritative map on startup.
3. Persist placement/destruction/fracture changes immediately.

Exit criteria:

- player-made cubes survive restart and retain owner linkage

## Phase 6: Item And Inventory Foundation

1. Introduce runtime item instance and inventory slot models.
2. Add server-side ownership rules:
   - world item
   - player-owned inventory item
   - equipped/hotbar slot item
3. Persist item instances and slot assignments.
4. Add pickup/drop/equip/unequip persistence hooks.

Exit criteria:

- the server knows which item belongs to which player and which slot

## Phase 7: Replication Alignment

1. Remove remaining shared/global mode assumptions from snapshots.
2. Extend actor/item replication DTOs as needed.
3. Add world item replication for dropped pickups.
4. Add equipped-item replication so other players can see held-item changes.

Exit criteria:

- client presentation aligns with the new server-owned persistence state

## Phase 8: Tests

1. Repository round-trip tests for all persistence families.
2. Runtime restore tests:
   - players
   - enemies
   - cubes
   - items
3. Multiplayer authority tests:
   - per-player mode isolation
   - per-player stat isolation
   - reconnect position restore
   - inventory ownership restore

Exit criteria:

- persistence and authority behavior is regression-covered

## Recommended Order

Implement in this order:

1. player persistence
2. enemy persistence
3. cube persistence
4. item/inventory persistence
5. replication follow-through

This preserves the current multiplayer core while expanding the server-owned world in controlled slices.
