Purpose: Define the first destructible-items slice for `dream`, where placed cubes can take combat damage and be destroyed after enough hits while preserving the current capability-based combat architecture.
Status: Approved in chat, pending implementation.
Scope: Combat-capable placed cubes above the floor, destruction orchestration, and state preservation across map rebuilds.

# Summary

The current project already has reusable combat primitives, but world items are not mapped into them. This spec introduces a separate world-object combat mapping layer so placed cubes can participate in damage, floating combat feedback, and destruction without forcing permanent HP state into every `Cube`.

The first gameplay slice enables destructibility only for placed cubes above the floor. The architecture should remain broad enough to support future breakable floor tiles or richer props later.

# Goals

- allow combat-mode hits to damage eligible placed cubes
- destroy placed cubes after lethal damage
- keep `Cube` as a world/render/physics piece rather than a full gameplay entity
- use existing combat primitives such as `HealthState` and `CombatActorHandle`
- preserve or explicitly reconstruct destructible placed-cube state during map rebuilds

# Non-Goals

- make the base floor destructible in this first gameplay slice
- add loot drops or item fragments
- introduce enemy combat loops
- add a full material-health or armor system
- redesign the entire map/world architecture

# Design

## Current-State Fit

Placed cubes currently exist in four main ways:

- as `Cube` world objects
- as map/grid occupancy
- as optional Bullet rigid bodies
- as render candidates

They do not currently exist as HP-bearing combat objects. The only real gameplay combat participant today is the player via `PlayerCombatant`.

That means destructible items are not blocked by missing combat primitives. They are blocked by missing object registration and lifecycle mapping.

## Core Architecture

The recommended architecture is a separate destructible-item combat layer.

Responsibilities are split like this:

- `Map`: cube existence, occupancy, render collections, spatial lookup, and world lifecycle
- destructible-item registry or adapter layer: `HealthState`, `CombatActorHandle`, anchor behavior, and destructibility mapping for eligible cubes
- `PlayScene`: hit resolution, damage application, destruction orchestration, and rebuild-state preservation policy
- render/HUD: passive reflection of combat and destruction results

This keeps combat capability optional and avoids turning `Cube` or `Map` into combat-heavy god objects.

## Registration Policy

For the first pass:

- placed cubes above the floor are eligible for destructible registration
- floor cubes remain excluded by policy

When a new placed cube is created, the system should also register a destructible-object record for it. That record should own:

- a `HealthState`
- a `CombatActorHandle`
- anchor behavior for floating combat text
- any lightweight policy flags needed for future extensibility

## Combat Hit Resolution

Combat mode should resolve real world targets instead of always publishing demo damage anchors.

The first pass should:

1. perform a forward combat hit query
2. resolve the result to a world cube through physics or map lookup
3. ask the destructible registry whether that cube is combat-capable
4. apply damage through the registered `HealthState`
5. publish feedback using the object’s own combat handle

If no destructible target is hit, combat mode can still fall back to non-destructive demo impact feedback if desired, but item damage should use the real target path.

## Destruction Lifecycle

When a registered placed cube reaches zero HP, the system must remove it cleanly from:

- the world grid
- `SolidCubes`
- the placed-cube spatial index
- Bullet rigid-body tracking and world membership
- the destructible-item registry

Destroyed cubes must stop rendering, stop colliding, and stop responding to later combat queries.

## Rebuild Preservation Policy

Floor-size rebuilds already copy surviving placed cubes into a rebuilt `Map`. The destructible-item system must define what happens to combat state during that transition.

Preferred policy for the first pass:

- preserve destructible state for surviving placed cubes when the implementation surface remains manageable

Fallback policy if needed:

- re-register surviving copied cubes deterministically and document that health resets during rebuilds

Preservation is preferred because it better matches player expectation and reduces special-case gameplay behavior.

# Data Flow

1. A placed cube is added to the world.
2. The cube is registered as a destructible object if it is eligible.
3. The player uses combat mode.
4. A combat hit query resolves a target in the world.
5. If the target cube is registered, the scene applies damage through its `HealthState`.
6. Floating combat text uses the object’s combat anchor.
7. If HP reaches zero, the cube is removed from world, physics, and registry state.
8. If the map is rebuilt, surviving destructible placed-cube state is preserved or reconstructed according to the chosen policy.

# Edge Cases

- floor cubes should remain immune in this first slice even if they are hittable by query
- missing registry entries for a hit cube should fail safely rather than crash the combat path
- destruction should not leave stale rigid bodies or stale cube references in render/spatial collections
- rebuild paths must not resurrect previously destroyed cubes
- copied placed cubes must not duplicate destructible registrations

# Testing

Implementation should add focused tests for:

- placed cubes registering as destructible while floor cubes do not
- successful damage application to a registered placed cube
- non-lethal hits preserving the cube while reducing HP
- lethal hits removing the cube cleanly from world and physics state
- floating combat feedback anchoring on the cube rather than on the player
- floor rebuild paths preserving or correctly reconstructing destructible placed-cube state

# Implementation Notes

- prefer a focused registry or adapter type over adding HP fields directly to `Cube`
- keep hit-resolution orchestration in `PlayScene`
- keep the first pass small: placed cubes first, broader breakable-object categories later
