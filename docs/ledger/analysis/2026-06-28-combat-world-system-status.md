Purpose: Audit the current combat and world-system roadmap status after the first creature, event, HUD, and destructible-item slices.
Status: Analysis
Scope: Combat capability layer, scene-local events, destructible world objects, and the remaining planned work for broader combat/world gameplay

# Summary

The core foundation is now in place:

- scene-local events exist and are used in production
- the player participates in the shared combat capability model
- floating damage is event-driven
- placed cubes can be damaged and destroyed through the combat path

The project is no longer at "combat prototype only" status. It has a real first-pass combat/world loop.

However, several roadmap slices remain intentionally incomplete:

- no generalized creature or enemy runtime exists yet
- no combat-stat or modifier framework exists yet
- no broader world-object taxonomy exists beyond placed cubes
- the event model is established, but only a small subset of planned event families is actually in use

# What Is Landed

## Creature support foundation

The original foundation plan is materially implemented.

Current production pieces include:

- `IDamageable`
- `ICombatAnchorProvider`
- `HealthState`
- `CombatActorHandle`
- `CreatureKind`
- `PlayerCombatant`

This is enough to support:

- player HP
- shared damage receipts
- anchor-based combat feedback
- non-player combat participants through adapters

## Scene-local event architecture

The event-driven refactor is partially but meaningfully landed.

Current production pieces include:

- `SceneEventHub`
- `DamageAppliedEvent`
- `ActorDiedEvent`
- `CombatFeedbackSystem`
- optional `EventTraceSystem`

This means the repo already follows the intended hybrid rule:

- simulation stays direct
- cross-system combat feedback uses scene-local facts

## Destructible world-item slice

The first planned world-object combat mapping is also landed.

Current production pieces include:

- `WorldObjectRegistry`
- `WorldObjectRecord`
- real world hit resolution against cubes
- lethal cube removal from world, physics, and registry state
- destructible-state preservation across floor rebuilds

This is the first real bridge between world objects and the combat system.

# What Is Only Partially Landed

## Creature support is still actor-light

The architecture supports more than the player, but the actual runtime population is still minimal.

Today the scene has:

- the player as a real combat-capable actor
- destructible placed cubes as adapted object actors

It does not yet have:

- enemy actors
- NPC or creature registries
- reusable scene queries over all combat-capable actors
- actor factions, teams, or alignment rules

So the capability model is correct, but the scene-level actor ecosystem is still thin.

## Event-driven architecture is still narrow

The event model is real, but its adoption is currently focused on combat feedback.

Still missing from the broader event roadmap:

- placement or destruction events for world mutation
- runtime settings events beyond the existing `GameSettings` callbacks
- interaction events such as a first-class "primary action performed" fact
- world-object lifecycle events that future audio/VFX/loot systems can subscribe to

This is not a flaw. It simply means the event system is established but not yet broadly exploited.

## Destructible items are still cube-specific

The first-pass design deliberately avoided overgeneralizing, which was the right call.

But the current implementation is still tied to:

- placed cubes
- fixed cube HP policy
- direct scene-owned registration

Still missing for a broader world-object model:

- breakable prop definitions
- material-driven durability rules
- loot/drop or post-destruction reactions
- a more general world-object descriptor that is not cube-only

# What Is Still Unfinished From The Earlier Plans

## 1. Combat stats framework

This is the clearest unimplemented planned slice.

The repo still does not contain:

- `CombatStat`
- `CombatStatModifier`
- `CombatStatType`
- cached modifier evaluation
- attack power / defense / attack cadence as first-class runtime data

Current combat is still driven by direct values:

- `HealthState`
- ad hoc random hit amounts

That is acceptable for the current prototype stage, but it means combat depth is still shallow by plan.

## 2. Generic combat/world object lifecycle

The world can destroy placed cubes, but there is not yet a generalized path for:

- props
- interactables
- enemies occupying world space
- world objects with custom death or destruction consequences

The next layer needs a broader world-object abstraction or registration pattern, not just more cube-specific special cases.

## 3. Wider scene-event adoption for world mutations

Right now destruction publishes combat facts, but world mutation itself is still mostly direct orchestration inside `PlayScene`.

Good next event candidates are:

- `CubeDestroyedEvent`
- `CubePlacedEvent`
- `WorldObjectRemovedEvent`
- `InteractionModeChangedEvent`

Those would let audio, VFX, analytics, and future quest or scripting systems react without adding more direct scene coupling.

## 4. Enemy-ready combat loop

The foundation can support enemies, but the scene still lacks the standard loop for:

- target acquisition
- attack ownership
- hostile damage exchange
- death cleanup for non-player actors
- future AI or behavior integration

This is likely the most meaningful gameplay gap after the current world-destructible slice.

# Recommended Next Order

## Near-term

1. Add world-mutation events for placement and destruction.
2. Introduce a broader world-object combat registration shape so destructibles are not permanently cube-only.
3. Replace ad hoc damage values with a very small combat-stat slice only when a second real combat actor type lands.

## Mid-term

1. Add the first enemy actor using the existing capability model.
2. Route enemy damage and death through the same event and feedback pipeline.
3. Expand destruction reactions into audio, VFX, or drops through scene-local subscriptions.

# Recommendation

Do not jump straight into a full RPG stat matrix or a heavyweight entity hierarchy.

The healthiest next move is:

1. broaden world mutation events
2. generalize destructible world-object registration one step beyond cubes
3. add the first enemy/runtime creature using the same capability and event model

That sequence preserves the good architecture choices already made while turning the current slice into a real combat/world gameplay framework.
