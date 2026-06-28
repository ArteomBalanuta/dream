Purpose: Define the first real enemy actor slice for `dream` using an animated wolf model and the existing combat/event/render foundations.
Status: Approved in chat, pending implementation.
Scope: First enemy actor, animated wolf asset integration, roaming/chasing behavior, enemy combat loop, and scene-local death/feedback wiring

# Summary

`dream` now has a functional player combat slice, destructible world objects, floating damage, and scene-local combat events. The next highest-value gameplay step is a true non-player combat actor.

This spec introduces a first-pass wolf enemy that:

- exists as a real world actor
- uses an imported animated wolf glTF/glb asset
- plays a movement animation while roaming or chasing
- can be damaged and killed by the player
- can damage the player on a cooldown when in range

The goal is to validate actor-vs-actor combat and animated non-player creatures without overbuilding a full AI or entity hierarchy.

# Goals

- add one animated wolf enemy as the first non-player combat actor
- integrate a repo-owned wolf glTF/glb asset with legal reuse suitable for the project
- play a looped movement animation while the wolf is moving
- support at least idle and movement visual states if both clips are available
- let the wolf chase the player with a simple movement behavior
- let the wolf damage the player on a cooldown when close enough
- route wolf damage, death, and feedback through the current combat/event pipeline
- keep gameplay, rendering, and animation ownership reasonably separated

# Non-Goals

- a full enemy framework for many creature families
- behavior trees, navmeshes, or complex steering
- pack tactics or faction systems
- loot tables, drops, or corpse persistence
- a full shared stat/modifier framework
- advanced animation blending

# Asset Strategy

The first enemy pass should use a wolf asset sourced from a permissive library such as Quaternius / Poly Pizza rather than from a non-commercial-only source.

Preferred direction:

- download a CC0 or equivalently permissive low-poly animated wolf asset
- import it into a repo-owned runtime asset folder under `dream/animations/`
- resolve it through `AssetPaths` and `ModelAssets`

The asset should ideally contain:

- `Idle`
- `Walk` or `Run`

If exact clip names differ, the runtime should map them explicitly instead of assuming the viewmodel clip contract.

# Design

## Runtime Shape

The first wolf should not be pushed directly into `PlayScene` as ad hoc state. Instead, this slice should introduce focused enemy-side runtime types with clear responsibilities.

Recommended shape:

- `EnemyActor`
  - owns world position, simple movement behavior, and scene-visible enemy state
- `EnemyCombatant`
  - owns health, combat handle, combat anchor, and outgoing attack cadence
- `EnemyVisualState` or equivalent small state holder
  - owns which animation clip should currently play

The exact names can vary, but the split should preserve these boundaries:

- combat participation stays in combat-focused code
- movement/chase logic stays in enemy runtime behavior
- render-facing animation state stays lightweight and display-oriented

## Scene Ownership

`PlayScene` remains the scene orchestrator.

For the first pass, `PlayScene` should own:

- wolf spawn at startup
- enemy update sequencing
- enemy render participation setup
- enemy removal after death

However, `PlayScene` should not own the internals of:

- wolf chase math
- enemy combat cooldown logic
- per-enemy animation state transitions

If the scene needs a collection, use a small scene-local enemy registry/list rather than hardcoding a single loose variable pattern that cannot grow cleanly.

## Movement And Behavior

The first wolf should use simple direct behavior:

1. If the player is outside aggro range, idle or lightly patrol in a small local area.
2. If the player enters aggro range, the wolf turns and moves toward the player.
3. If the player is within attack range, the wolf stops closing and attacks on a cooldown.

This should be implemented with lightweight, deterministic scene math rather than a full pathfinding stack.

Recommended first-pass constants:

- aggro range: medium
- attack range: short melee range
- move speed: slightly slower than player noclip, but reasonable for ground pursuit
- attack cadence: explicit enemy cooldown, not every frame

## Combat Integration

The wolf should participate in the same combat foundation as the player and world objects:

- `IDamageable`
- `ICombatAnchorProvider`
- `HealthState`
- `CombatActorHandle`

When the player hits the wolf:

- the wolf takes damage through the shared combat path
- `DamageAppliedEvent` publishes floating damage
- lethal damage publishes `ActorDiedEvent`
- the wolf is removed from active scene/runtime participation

When the wolf attacks the player:

- the player takes damage through `PlayerCombatant.ApplyDamage(...)`
- damage should also publish the same feedback/event path used elsewhere
- HUD HP should update automatically through the existing player combat state

## Animation Behavior

The wolf asset should use named clips resolved explicitly for enemy behavior. Do not reuse the viewmodel clip contract.

Recommended first-pass mapping:

- idle state -> mapped idle clip
- moving/chasing state -> mapped walk/run clip

If the wolf asset includes an attack clip, that can be queued in a later pass, but it is not required for this first milestone. The critical requirement is a working movement animation.

## Rendering Integration

The wolf should render through the existing animated-model path rather than through cube-specific rendering.

This likely means introducing a world-actor render path or a focused enemy render path that:

- resolves the wolf model through `ModelAssets`
- updates clip playback during scene update
- renders the model in world space with a proper model matrix

The design should avoid abusing `Cube.Animation` just to get a wolf on screen. Enemies are no longer cubes and should not masquerade as them.

## Testing

Implementation should add focused tests for:

- enemy combatant damage/death behavior
- enemy cooldown-based attack behavior
- simple chase-state transitions based on player distance
- clip-selection state changes for idle vs moving
- scene removal or inactive-state behavior after wolf death

Asset import itself does not need deep automated tests, but path resolution and clip mapping should be validated enough to fail clearly if the asset changes.

# Risks

## Risk: Asset-driven assumptions leak into gameplay code

If clip names or model paths are hardcoded inside scene orchestration, later asset replacement becomes painful.

Mitigation:

- isolate wolf asset metadata or clip mapping into focused enemy/model config

## Risk: `PlayScene` becomes the enemy god class

If spawning, chase math, combat rules, animation selection, and removal all accumulate in `PlayScene`, this slice will regress the architecture work already done.

Mitigation:

- keep enemy movement/combat/visual logic in focused collaborators

## Risk: Building a general AI framework too early

The first wolf only needs enough behavior to validate combat and animation integration.

Mitigation:

- keep behavior explicit and small
- defer generalized AI systems until a second enemy or behavior family exists

# Recommendation

Implement a single animated wolf as the first enemy actor, with:

- permissive repo-owned asset integration
- simple aggro/chase/melee behavior
- shared combat/event participation
- movement animation as the required visual baseline

This is the cleanest next proof point for the project’s current architecture and should happen before broader event expansion or a generalized modifier framework.
