# Scene Event Architecture

Purpose: define the approved event-driven architecture direction for decoupling gameplay, presentation, and future creature/combat systems.
Status: proposed
Scope: scene-local domain events, observer-style system reactions, and render/play boundary rules

## Goal

Introduce a scene-scoped event architecture that improves decoupling across gameplay, HUD, combat feedback, and future reactive systems without turning the engine into an opaque global message bus.

## Design Summary

The project should adopt a hybrid architecture:

- explicit per-frame simulation remains direct and ordered
- cross-system facts are published as typed domain events
- observer systems react to those events
- event lifetime and ownership stay local to the active scene

This means the engine should become more event-driven where that improves decoupling, but not everywhere.

## Core Principles

### 1. Keep simulation explicit

The following should remain direct, deterministic, and ordered:

- input capture
- gameplay update sequencing
- physics stepping
- transform synchronization
- render pass ordering

These systems are easier to reason about when their order is explicit.

### 2. Event-drive cross-system reactions

Events should be used when one system should not need to know who reacts.

Examples:

- combat feedback
- actor death reactions
- sound triggers
- particles and hit flashes
- debug instrumentation
- later quest or scripting hooks

### 3. Scope events to the scene

The event hub should be owned by `PlayScene`, not by a global singleton.

That means:

- scene lifetime owns subscription lifetime
- tests can build isolated scene systems
- multiple scene types remain possible later
- control flow stays more local and understandable

### 4. Events describe facts, not wishes

Published events should represent something that already happened.

Good:

- `DamageAppliedEvent`
- `CubePlacedEvent`
- `ActorDiedEvent`
- `SettingsMenuToggledEvent`

Avoid treating the event bus as a generic command queue for basic gameplay flow.

## Recommended Architecture

### Scene Root

`PlayScene` remains the orchestration root, but it gains one additional responsibility:

- own the `SceneEventHub`

`PlayScene` should not become thinner by disappearing. It should become thinner by wiring specialized systems together and publishing facts instead of directly triggering every reaction.

### Event Hub

Add a compact typed event hub, for example:

- `SceneEventHub`
- typed `Publish<TEvent>(TEvent eventData)`
- typed `Subscribe<TEvent>(Action<TEvent> handler)`

Design requirements:

- no global singleton
- no reflection-heavy design
- no string-key event names
- simple unsubscribe/lifetime handling
- easy to test with focused unit tests

### Domain Event Families

Recommended early event families:

- interaction events
- combat events
- world mutation events
- runtime mode events

Initial concrete events:

- `PrimaryUseTriggeredEvent`
- `CubePlacedEvent`
- `DamageAppliedEvent`
- `ActorDiedEvent`
- `FloorSizeChangedEvent`
- `SettingsMenuToggledEvent`

### Observer Systems

Early subscribers should be small and purpose-specific.

Examples:

- `CombatFeedbackSystem`
  - subscribes to `DamageAppliedEvent`
  - turns domain events into floating combat text requests
- future `AudioFeedbackSystem`
  - subscribes to combat or interaction events
- future `WorldReactionSystem`
  - subscribes to placement or destruction events

These systems should react to events without forcing gameplay systems to know they exist.

## Render And Presentation Boundary

### Rendering rule

`Render` and its collaborators should remain presentation-oriented.

They may:

- draw scene state
- draw transient feedback state
- present HUD controls
- return user requests

They should not be the primary home of gameplay consequences.

### HUD rule

`HudRenderer` should gradually move away from being directly called by gameplay logic for specific reactions.

Instead:

- gameplay publishes domain events
- a subscriber translates those events into HUD-facing presentation state
- the HUD draws the resulting state

This is especially important for combat text, because the same event stream should later be able to feed:

- floating numbers
- sound
- hit flash
- controller rumble
- analytics/debug hooks

without changing gameplay publishers.

## Combat Direction

The recent combat foundation should continue in the same capability-first style.

The combat layer should publish facts such as:

- damage applied to an actor
- actor health reaching zero

That keeps combat actor identity small and reusable while allowing multiple observer systems to react independently.

The event architecture should complement the capability layer rather than replace it.

## What Stays Direct Versus Evented

### Keep direct

- `PlayerController` capturing movement and interaction intent
- `PlayerMotor` applying movement and jump behavior
- Bullet stepping and rigid-body synchronization
- explicit render pass ordering
- map rebuild execution after an approved floor-size change

### Move toward evented reactions

- floating combat text
- future damage sounds
- future death reactions
- future breakable-object reactions
- future debugging and telemetry hooks

## Phase Boundaries

### Phase 1: event foundation

- add the scene-local hub
- add typed publish/subscribe support
- add unit tests for subscription and dispatch behavior

### Phase 2: combat feedback decoupling

- replace direct HUD combat-text triggering from `PlayScene`
- publish combat/damage events instead
- add a subscriber that owns the translation to floating-text state

### Phase 3: render/play boundary cleanup

- reduce runtime-state mutation inside render-owned code
- keep menu UI presentation in render/HUD
- move consequence handling toward scene/application systems

### Phase 4: future system adoption

- route creatures, enemies, breakables, audio, VFX, and other reactive systems through the same local event model

## Success Criteria

- gameplay systems no longer need direct knowledge of combat-text presentation ownership
- the first event-driven slice clearly removes a real dependency edge
- scene update order remains explicit and easy to follow
- the architecture stays local and testable instead of becoming a global bus
- future reactive systems can subscribe without forcing gameplay publishers to change

## Non-Goals

This design does not propose:

- a full ECS rewrite
- a global engine-wide event bus
- event-driving every frame-level subsystem
- replacing direct gameplay sequencing with generic message traffic

## Testing Strategy

The event architecture should be proven through focused tests:

- typed hub subscription and dispatch
- subscriber invocation order where relevant
- combat feedback behavior driven by published events rather than direct HUD calls
- regression tests confirming gameplay flow still behaves as expected

Full verification should continue to include:

- `dotnet build dream\\dream.csproj`
- `dotnet test dream.Tests\\dream.Tests.csproj`

## Approved Direction

Use a scene-scoped observer/event pattern as a targeted decoupling tool.

The project should become:

- more event-driven for cross-system reactions
- still explicit for simulation and rendering order

That is the recommended best-practice fit for the current `dream` architecture.
