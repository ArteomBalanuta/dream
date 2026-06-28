Purpose: Define the remaining roadmap for adding the first real enemy-ready combat loop to `dream`.
Status: Analysis
Scope: Enemy actor support, enemy combat exchange, and future AI-ready scene integration

# Goal

Add the first true non-player combat actor so the project moves from player-vs-object combat into actor-vs-actor gameplay.

# What Exists Already

The repo already has the right foundation for a first enemy slice:

- `IDamageable`
- `ICombatAnchorProvider`
- `HealthState`
- `CombatActorHandle`
- `CreatureKind`
- `PlayerCombatant`
- `DamageAppliedEvent`
- `ActorDiedEvent`
- floating combat text

# What Is Missing

- a concrete enemy combat adapter
- enemy registration/lifetime inside the scene
- enemy damage intake and death cleanup
- enemy target presence in world space
- the first hostile or neutral attack loop

# Recommended Phases

## Phase 1: Static Enemy Actor

Add one simple enemy actor with:

- world position
- health
- combat anchor
- combat handle
- damage and death participation

This phase should not include AI yet. The first goal is to prove the actor model against a non-player combatant.

## Phase 2: Scene-Owned Enemy Registry

Add a scene-local registry or collection that makes enemies explicit scene participants rather than ad hoc objects.

This should support:

- spawn
- update
- render lookup or anchor lookup
- death cleanup

## Phase 3: First Hostile Combat Loop

Add a basic combat exchange path:

- enemy can be hit by the player
- enemy can optionally damage the player
- death routes through the same event pipeline

This should stay intentionally small:

- no behavior tree requirement
- no full faction system requirement
- no loot table requirement yet

## Phase 4: AI-Ready Hooks

Once the enemy loop exists, add extension seams for:

- chase or idle behavior
- target acquisition
- cooldown-based attack cadence
- future enemy archetypes

# Recommended File-Level Focus

- `dream/PlayScene.cs`
- new enemy-specific runtime types such as `EnemyCombatant`, `EnemyActor`, or similar focused collaborators
- shared combat event subscribers only if they support real enemy behavior

# Risks

- overbuilding a generic entity hierarchy too early
- pushing enemy logic directly into `PlayScene`
- mixing AI, combat, and rendering responsibilities into one type

# Recommendation

The healthiest next step is a deliberately small first enemy that proves:

- non-player combat participation
- enemy death cleanup
- shared feedback/event flow

After that, the project can decide whether AI or broader world-object growth should come next.
