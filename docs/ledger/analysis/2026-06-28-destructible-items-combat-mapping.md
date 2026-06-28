Purpose: Map how world items currently exist in `dream`, identify whether they already participate in combat state such as HP, and define the missing links required for destructible items.
Status: Analysis
Scope: `Cube`, `Map`, physics binding, combat primitives, and the first destructible-items slice

# Summary

`dream` already has reusable combat primitives such as `IDamageable`, `HealthState`, `ICombatAnchorProvider`, and `CombatActorHandle`, but world items are not yet mapped into that model.

Placed cubes currently exist as:

- world/data objects (`Cube`)
- map/grid occupancy
- optional Bullet rigid bodies
- render candidates

They do not currently exist as:

- combat-capable objects
- health-bearing items
- destroyable world entities with a registered combat lifecycle

So the project does not need a brand-new combat foundation for destructible items. It needs a world-object combat mapping layer that bridges existing placed cubes into the already-established combat capability model.

# Current Object Model

## `Cube` is a world/render/physics piece, not a gameplay entity

`dream/Cube.cs` currently stores:

- position
- solidity and a few world flags
- animated model reference
- shader name

It does not store:

- health
- combat kind
- combat events
- destruction lifecycle

This is an important boundary. `Cube` is still a generic world piece rather than a gameplay-owned combat object.

## `Map` owns cube existence and occupancy

`dream/Map.cs` is the current source of truth for cube creation and residency.

It:

- materializes the base floor during initialization
- adds placed cubes through `AddCube(...)`
- stores cubes in `WorldGrid`
- keeps a flat `SolidCubes` render source
- keeps non-floor cubes in the placed-cube spatial index
- copies placed cubes into rebuilt maps during floor resizing

What `Map` does not do today:

- create HP-bearing item records
- register cubes into combat lookup structures
- destroy cubes through a removal API

This means item destruction cannot be added cleanly by touching only the combat layer. `Map` needs a controlled removal-capable lifecycle or a companion orchestration layer that can ask `Map` to remove a cube.

## Physics currently points back to `Cube`

`dream/WorldPhysicsBinder.cs` stores each cube body with:

- `UserObject = (cube, position)`

That is useful because a future hit query can map a Bullet body back to the corresponding `Cube`.

However, the current physics link only gives access to the cube object itself. It does not map to:

- a destructible object record
- HP
- a combat handle

So the physics path already has half of what is needed for destructible item combat, but it still lacks a combat-registration lookup.

# Current Combat Model

## The reusable combat primitives are already in place

The current combat foundation already supports non-player objects in principle:

- `IDamageable`
- `HealthState`
- `ICombatAnchorProvider`
- `CreatureKind`
- `CombatActorHandle`

This is the right architectural direction. The repo does not need to invent a second HP system for items.

## Only the player is truly integrated today

The only real gameplay-owned combat participant currently wired into the scene is the player through `PlayerCombatant`.

That gives the player:

- a `HealthState`
- a `CombatActorHandle`
- a combat anchor
- optional `ActorDiedEvent` publication on fatal damage

No equivalent world-object adapter exists yet for placed cubes.

## Demo combat feedback currently bypasses real item targeting

The current combat-mode click flow now publishes damage feedback at a forward impact anchor for visibility, but it is still demo-target-driven rather than item-target-driven.

That means:

- combat click feedback can appear in front of the player
- but it does not yet resolve a hit destructible item
- and it does not yet apply damage to a placed-cube-backed `HealthState`

# Gap Analysis

The missing capabilities are:

## 1. No destructible-item registration

There is no registry that answers:

- which cubes are destructible
- what health object belongs to each cube
- what combat handle belongs to each cube

## 2. No item hit-resolution bridge

The scene currently lacks a proper object-targeting path that turns a combat click into:

- a physics or map hit result
- a `Cube`
- a destructible object record
- a damage application

## 3. No removal lifecycle for destroyed placed cubes

There is currently no bounded removal path that updates:

- `WorldGrid`
- `SolidCubes`
- placed-cube spatial indexing
- Bullet rigid bodies
- combat registration

Without that, “destroy the item when HP reaches zero” would leave stale world or physics references.

## 4. No rebuild-preservation rule for destructible state

`Map.CopyPlacedCubesTo(...)` currently preserves existence of placed cubes during floor-size rebuilds, but not any future combat state.

If destructible items are added, rebuild behavior must explicitly define whether:

- health persists across rebuilds
- health is re-registered from copied cubes
- destroyed cubes stay absent

# Recommended Direction

## Use a separate world-object combat mapping layer

The best fit is not to put HP directly onto `Cube`.

Instead:

- keep `Cube` as world/render/physics state
- add a destructible world-object adapter or registry
- let that registry own `HealthState`, `CombatActorHandle`, and anchor behavior for selected cubes

This follows the existing capability-based combat design and avoids bloating all cubes with permanent combat state.

## Enable placed cubes first, keep architecture broader

For the first slice:

- only placed cubes above the floor are destructible
- floor cubes remain excluded by policy

But the design should not hard-code “only placed cubes can ever be destructible.” It should allow future opt-in expansion to breakable floor tiles or other object kinds.

## Keep ownership split explicit

Recommended split:

- `Map`: cube existence, occupancy, spatial lookup, world lifecycle
- destructible-item registry: HP/combat mapping for eligible cubes
- `PlayScene`: hit resolution, damage application, destruction orchestration
- render/HUD: passive reflection of results

# Risks

## Risk: putting HP directly on `Cube`

Consequence:

- generic world pieces become permanently gameplay-heavy
- combat logic leaks into low-level world storage

## Risk: putting destruction logic directly into `Map`

Consequence:

- `Map` grows into a combat/world god object
- scene-owned orchestration becomes harder to preserve

## Risk: not defining rebuild behavior

Consequence:

- destructible state resets unexpectedly
- copied cubes lose HP/state consistency

# Final Recommendation

The project is already structurally ready for destructible items at the combat-primitive level. The missing work is a mapping layer between placed cubes and combat capability.

The first implementation should:

1. register placed cubes as destructible world objects through a separate registry
2. resolve combat clicks against real world targets
3. apply damage through `HealthState`
4. remove cubes cleanly when health reaches zero
5. preserve or explicitly reconstruct destructible state during map rebuilds
