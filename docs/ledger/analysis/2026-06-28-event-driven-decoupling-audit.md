# Event-Driven Decoupling Audit

Purpose: audit the current scene, gameplay, and rendering boundaries before introducing broader observer-style architecture.
Status: complete
Scope: `PlayScene`, `Render`, `HudRenderer`, `GameSettings`, and the immediate gameplay-to-presentation seams

## Summary

`dream` is no longer a single giant shell class, but it is still primarily direct-call driven rather than event-driven.

The codebase already has several healthy structural moves in place:

- `Game` is mostly a platform shell
- `PlayScene` owns active gameplay orchestration
- rendering responsibilities have been split into `WorldRenderer`, `DebugRenderer`, `ViewModelRenderer`, and `HudRenderer`
- `GameSettings` already exposes targeted observer-style events for a few runtime changes
- the new combat support layer is capability-based instead of inheritance-heavy

However, the project is not yet operating as an event-driven game architecture. The main flow is still imperative coordination, and several presentation systems are still triggered directly from gameplay code.

## What Is Already Decoupled

### Shell versus scene

The earlier `Game` to `PlayScene` split was the correct first move.

- `Game` now owns the OpenTK shell lifecycle
- `PlayScene` owns the active world, player, physics, rendering host, and update ordering

This means the repo now has a stable place to host scene-scoped orchestration and scene-local event infrastructure.

### Render surface extraction

The render layer is no longer one flat surface:

- `WorldRenderer` handles world drawing
- `DebugRenderer` handles collision overlays
- `ViewModelRenderer` handles first-person draw behavior
- `HudRenderer` handles HUD-facing runtime behavior

That is meaningful progress toward separation of concerns, even though some gameplay/presentation coupling still remains one layer above.

### Focused settings events

`GameSettings` already exposes:

- `ResolutionApplied`
- `FloorSizeApplied`
- `CollisionDebugVisibilityChanged`
- `RuntimeStateChanged`

This is the clearest existing observer-style pattern in the repo. It proves the codebase can support event publication where the events are narrow, meaningful, and tied to facts that already happened.

### Capability-based combat direction

The new combat support work uses:

- `IDamageable`
- `ICombatAnchorProvider`
- `HealthState`
- `CombatActorHandle`
- `PlayerCombatant`

That is a good fit for later event-driven combat reactions because it avoids deep inheritance and keeps actor identity small and explicit.

## Where The Coupling Still Lives

### `PlayScene` still directly coordinates too many concrete reactions

`PlayScene.Update()` currently:

- refreshes viewmodel pose
- handles settings-toggle input
- checks primary use
- directly triggers a viewmodel use animation
- directly spawns floating combat text through the HUD
- directly advances viewmodel playback
- directly steps physics
- directly updates the player
- directly places cubes
- directly ticks HUD runtime state
- directly runs physics-to-render sync

This is valid as an early orchestration layer, but it means cross-system behavior still depends on direct knowledge rather than shared domain notifications.

### Gameplay still reaches into presentation behavior

The clearest example is the floating combat text path:

- `PlayScene.Update()` detects a gameplay input event
- then directly calls `_render.Hud.SpawnDamage(...)`

That means gameplay currently knows:

- there is an HUD-managed feedback system
- how damage text should be triggered
- which rendering-adjacent object owns that transient presentation state

This is exactly the kind of coupling that observer-style domain events should remove.

### `HudRenderer` is both a presentation facade and a runtime feedback owner

`HudRenderer` currently:

- delegates to `Render.DrawHUD(...)`
- owns `FloatingCombatTextSystem`
- updates that runtime system
- exposes direct `SpawnDamage(...)` methods

This is workable, but it makes the HUD layer the first-class owner of a gameplay reaction mechanism rather than a subscriber to gameplay facts.

### `Render` still mixes low-level rendering with interaction/state application

`Render.DrawHUD(...)` and nearby helpers currently do more than rendering:

- build ImGui state
- draw overlay content
- collect HUD interaction changes
- apply some of those changes back into `GameSettings`

That means `Render` is still partly a renderer and partly a UI behavior host. It is less dense than earlier, but it is not yet presentation-only.

### Existing settings events are useful but not yet an engine-wide pattern

The `GameSettings` events are narrow and useful, but the broader game architecture does not yet follow the same rule:

- gameplay publishes facts
- observers react

Instead, most systems are still wired through direct method calls and manual sequencing.

## What Should Become Event-Driven

The right targets are cross-system reactions and side effects, especially when the publisher should not care who reacts.

Strong early candidates:

- combat feedback
- future audio cues
- future particles and hit reactions
- cube placement reactions
- actor death reactions
- settings-mode transitions that other systems care about

These are all places where one system can publish a fact and many observers may react without introducing hard dependencies.

## What Should Stay Explicit

Not every game subsystem should be event-driven.

The following should remain direct and ordered:

- input capture
- movement integration
- physics stepping
- transform sync
- deterministic per-frame scene update sequencing
- inner render loops

These are better served by explicit orchestration, predictable order, and data flow that is easy to step through in a debugger.

## Recommended Direction

Adopt a hybrid architecture:

- keep the simulation/update frame explicit
- introduce a scene-scoped domain event hub for cross-system reactions
- move presentation-side reactions behind subscriptions
- keep event scope local to the active scene instead of introducing a global engine bus

This is the best-practice fit for the current repo size and maturity. It improves decoupling without replacing understandable gameplay flow with opaque message traffic.

## Immediate Refactor Opportunities

### 1. Introduce a scene-local event hub

`PlayScene` is now the right owner for a `SceneEventHub`.

That hub should:

- live only for the active scene
- provide typed publish/subscribe behavior
- remain small and explicit
- avoid global singleton behavior

### 2. Move floating combat text behind gameplay events

The first clear consumer path should be:

- gameplay publishes `DamageAppliedEvent`
- a combat feedback system or HUD-facing subscriber turns that into floating text

This is the lowest-risk proof that the event architecture is actually buying decoupling.

### 3. Pull HUD state mutation out of low-level render ownership

The render layer should ideally:

- present UI
- return user requests
- avoid deciding gameplay consequences directly

That does not require removing all request-return patterns, but it does mean the system should move farther away from "renderer mutates runtime state."

### 4. Keep future creature/enemy work on the same event model

The new combat foundation should publish facts such as:

- damage applied
- actor killed
- interaction performed

That allows:

- HUD feedback
- sound
- VFX
- AI reactions
- logging/debug tools

to react without hard-linking all systems together.

## Risks To Avoid

### Global event bus drift

A single engine-wide event bus is tempting, but it would be too much for the current repo. It would increase hidden control flow and make debugging more difficult.

### Over-eventing

If every action becomes an event, the code will become harder to trace than it is today.

The rule should be:

- direct calls for ordered simulation work
- events for cross-system facts and reactions

### Premature generalization

The next implementation step should not attempt to solve:

- all gameplay messaging
- all UI messaging
- all scene transitions
- all AI communication

The event model should prove itself first in the most obvious coupling seams.

## Conclusion

The repo is on a good architectural path, but it is not yet properly event-driven.

The right next move is not a rewrite. It is a scene-scoped observer/event layer that:

- preserves explicit update order
- decouples gameplay from presentation reactions
- keeps `PlayScene` as the scene mediator, but a slimmer one over time
- gives future combat, creatures, audio, and VFX a shared integration path
