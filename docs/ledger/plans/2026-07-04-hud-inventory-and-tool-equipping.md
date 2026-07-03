Purpose: Break the approved HUD inventory and tool-equipping feature into execution-ready tasks.
Status: In Progress
Scope: Docs, authoritative runtime state, protocol updates, HUD implementation, held-item visuals, and tests.

## Phase 1: Documentation

1. Document the current DDL in `docs/server-persistence-ddl.md`.
2. Add an inventory/tool spec.
3. Record the implementation in the work log after verification.

Exit criteria:

- schema and inventory behavior are documented for future sessions

## Phase 2: Shared Inventory Contracts

1. Add shared inventory constants.
2. Add authoritative item/tool definition metadata.
3. Add shared inventory snapshot DTOs for protocol and HUD use.

Exit criteria:

- runtime, protocol, and HUD all share one inventory snapshot shape

## Phase 3: Server Runtime And Persistence Wiring

1. Load persisted item instances and slot assignments into the runtime.
2. Seed starter inventory for first-time players.
3. Build authoritative inventory snapshots by player name.
4. Persist equipped-slot changes.

Exit criteria:

- the server can restore and export per-player inventory state

## Phase 4: Protocol

1. Add an inventory equip action payload.
2. Add an inventory changed action result payload.
3. Extend join-accepted messages with inventory snapshots.

Exit criteria:

- the client receives initial inventory state and authoritative equip results

## Phase 5: Client UX

1. Add inventory runtime mode and cursor behavior.
2. Remap noclip toggle off `V`.
3. Add a standalone HUD inventory overlay with a `4x4` grid.
4. Send equip actions when an occupied slot is clicked.
5. Apply join/result snapshots locally and update the held-item viewmodel.

Exit criteria:

- inventory can be opened with `V`, hovered, and used to equip tools

## Phase 6: Verification

1. Add protocol tests for inventory payloads.
2. Add runtime/host tests for starter inventory and equip flow.
3. Add persistence tests for item/slot round-trips.
4. Add settings/runtime-state tests for inventory overlay behavior.
5. Run build and full test suite.

Exit criteria:

- the first-pass server-backed inventory slice is regression-covered
