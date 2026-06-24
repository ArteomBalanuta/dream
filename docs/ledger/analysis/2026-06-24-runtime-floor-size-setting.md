# Runtime Floor Size Setting Analysis

**Purpose:** Capture the current constraints and design pressure around adding a runtime floor-size setting to `dream`.

**Status:** Complete

**Scope:** Runtime settings UI, map rebuild behavior, player preservation, and cube migration

## User Goal

Allow the user to change the floor size at runtime from the settings menu, where a value such as `10` means a `10 x 10` floor, while preserving the current world state as much as possible.

## Current Constraints

### Map size is fixed at construction

`Map.cs` currently owns:

- `Width`
- `Height`
- `Depth`
- `Cube?[,,] Cubes`

These are set at construction time and there is no in-place resize path.

### Floor physics is a merged static collider

The current performance pass replaced per-tile floor physics with one merged floor collider. That was a good change, but it means a floor-size change is now a world-physics rebuild operation rather than a simple tile append.

### Player and map are both tied to live physics state

The player keeps:

- a `Map` reference
- a physics body in the shared Bullet world

The current map also registers bodies in the shared Bullet world, so resizing the floor must coordinate:

- old map-body removal
- new map-body creation
- player position preservation

## Root Design Implication

Runtime floor-size changes should be handled as a controlled world rebuild with state migration, not as ad-hoc in-place mutation of the current `Map`.

## Recommended Direction

Use a bounded rebuild-and-migrate flow:

1. User changes floor size in settings
2. UI emits an explicit apply request
3. `Game` creates a new `Map` with the requested square floor size
4. Existing solid cubes that still fit are copied into the new map
5. The old map bodies are removed from the physics world
6. The player's position is preserved if still valid, otherwise clamped to a safe in-bounds position
7. `Player.map` and `Game._map` are updated to the rebuilt map

## Preservation Rules

### Cubes

Preserve:

- placed cubes that remain within the new bounds

Drop:

- cubes that no longer fit after shrinking

Do not duplicate:

- floor cubes already recreated by the new map's own floor initialization

### Player

Preserve:

- current player position when still within the new floor bounds

Clamp:

- X and Z into the valid floor footprint

Keep safe:

- Y at a safe standing height if needed

## UI Recommendation

Expose:

- a single integer floor-size control in the settings menu

Apply model:

- explicit `Apply Floor Size` button

This avoids rebuilding the world repeatedly while the user drags a slider.

## Scope Boundary

Included:

- runtime floor-size setting
- world rebuild with cube migration
- player preservation
- docs and verification

Deferred:

- arbitrary rectangular floor width/depth controls
- undo/redo for world resize
- asynchronous rebuilds
