# Destructible Items First Pass

Purpose: Provide an execution-ready plan for mapping placed cubes into the combat system so they can take damage and be destroyed after enough hits.
Status: Planned
Scope: Architecture supports world destructibles broadly, while gameplay enables destructibility for placed cubes above the floor first.

## Goal

Allow combat-mode hits to damage eligible placed cubes and destroy them on lethal damage, while preserving the current capability-based combat architecture.

## Design Constraints

- Do not put permanent HP/combat state directly onto every `Cube`.
- Do not make floor cubes destructible in the first gameplay slice.
- Keep scene orchestration in `PlayScene`.
- Keep rendering passive.
- Keep the system compatible with later breakable props, enemies, and broader world objects.

## Proposed Architecture

### 1. Add a destructible world-object adapter/registry

Introduce a focused runtime layer that maps a placed `Cube` to:

- `HealthState`
- `CombatActorHandle`
- anchor behavior
- destruction eligibility

This can be scene-owned or world-owned, but should not turn `Map` itself into a combat container.

### 2. Add a real combat hit-resolution path

Replace the current combat demo-impact-only path with a world-target resolution flow:

- ray or forward hit query
- resolve a Bullet body or world cell
- map hit result back to `Cube`
- resolve `Cube` into a destructible object record if one exists

### 3. Add a clean cube-removal lifecycle

The game needs a supported path to remove a cube from:

- `WorldGrid`
- `SolidCubes`
- placed-cube spatial index
- Bullet body collections/world
- destructible-item registry

### 4. Define rebuild behavior

When the floor size changes and placed cubes are copied into a rebuilt `Map`, destructible-item state must either:

- be preserved explicitly
- or be re-registered deterministically from the copied cubes

The first pass should choose one policy and document it clearly.

## Recommended Execution Order

### Phase 1: Add world-item combat registration

- introduce a `DestructibleCube` or similarly narrow adapter
- introduce a registry keyed by `Cube` reference or stable cube identity
- register newly placed non-floor cubes
- leave floor cubes unregistered

### Phase 2: Add cube removal support

- add a bounded world removal path
- ensure render and spatial collections stay synchronized
- ensure Bullet bodies are removed safely

### Phase 3: Wire combat-mode hits into real item damage

- perform a combat hit query in front of the player
- resolve to a destructible cube if present
- apply damage through the registered `HealthState`
- publish floating damage through the object’s combat handle

### Phase 4: Destroy on zero HP

- detect lethal damage
- remove the registered cube cleanly
- drop combat registration
- ensure the destroyed item no longer renders or collides

### Phase 5: Preserve behavior across floor rebuilds

- choose and implement a preservation strategy
- add tests around copied placed cubes and retained destructible state

## Verification Targets

- placed cubes above the floor receive destructible registration
- floor cubes do not
- combat hits can resolve a placed cube target
- non-lethal hits reduce HP without removing the cube
- lethal hits remove the cube from render, physics, and world lookup
- floating damage anchors on the cube
- floor rebuilds preserve or correctly reconstruct destructible placed-cube state

## Recommended First Implementation Policy

For the first pass, preserve destructible state for surviving placed cubes across map rebuilds where possible. If direct state migration is too invasive for the first cut, re-register surviving copied cubes with documented reset behavior, but prefer preservation if the code surface remains manageable.

## Notes

This plan intentionally stops short of:

- full material-based health systems
- loot drops
- enemy combat loops
- generic prop-authoring pipelines

Those can follow once the cube-to-combat mapping and destruction lifecycle are proven.
