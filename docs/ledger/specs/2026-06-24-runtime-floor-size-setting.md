# Runtime Floor Size Setting Design

**Purpose:** Define the intended implementation for changing floor size at runtime while preserving the world state as much as possible.

**Status:** Approved direction

**Scope:** Settings UI, map rebuild flow, cube migration, and player preservation

## Goal

Allow the user to apply a square floor-size change at runtime from the settings menu, where a value such as `10` means a `10 x 10` floor, while preserving the player and any surviving placed cubes.

## Design Principles

- Treat floor-size changes as controlled world rebuilds.
- Preserve user-created world state when it still fits.
- Avoid repeated rebuilds during slider movement.
- Keep the runtime setting behavior explicit and predictable.

## Target State

After this work:

- the settings menu exposes a floor-size control
- the user can apply a floor-size change at runtime
- the map is rebuilt safely around the new floor size
- cubes that still fit are migrated into the new world
- the player remains in a valid in-bounds position

## Settings Design

### Runtime setting

Add a runtime floor-size setting to `GameSettings`.

Suggested behavior:

- current floor size is stored in settings
- a requested floor size can be staged through the UI

### UI behavior

The settings panel should expose:

- an integer floor-size slider or input
- an explicit `Apply Floor Size` button

The slider/input alone should not rebuild the world.

## Map Rebuild Design

### Configurable construction

`Map` should support configurable dimensions at construction time, with floor size mapped to:

- `Width = floorSize`
- `Depth = floorSize`

World height may remain fixed for this pass.

### Migration helper

`Map` should provide a bounded migration path that copies surviving non-floor solid cubes into a target map.

Rules:

- copy only solid cubes above the generated floor layer
- keep cubes that still fit inside target bounds
- drop cubes outside target bounds

This helper should be testable without physics or OpenGL.

### Physics rebuild

The old map bodies should be removed from the shared Bullet world before the new map is installed.

The new map should then:

- recreate the visual floor
- recreate the merged floor collider
- recreate any migrated dynamic cube bodies

## Player Preservation Design

When the world is rebuilt:

- preserve the player's current position if it remains valid
- otherwise clamp X and Z into the new floor bounds
- keep the player at or above a safe standing Y position
- reset the player's body transform and velocities so the new map takes effect cleanly

## Testing Design

Add tests for:

1. map construction with custom floor size
2. migration preserving in-bounds non-floor cubes
3. migration dropping out-of-bounds cubes
4. settings defaults or floor-size state behavior if useful

These tests should avoid requiring Bullet native loading or OpenGL context initialization.

## Scope Boundary

Included:

- square floor-size runtime setting
- explicit apply action
- map rebuild with migration
- player preservation/clamping

Deferred:

- live preview of floor size without apply
- rectangular width/depth editing
- preserving arbitrary dynamic rigid-body velocities during rebuild
