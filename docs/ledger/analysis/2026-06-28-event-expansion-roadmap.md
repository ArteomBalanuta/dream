Purpose: Define the remaining roadmap for widening scene-local event adoption without turning the project into a generic bus-driven engine.
Status: Analysis
Scope: Additional world, interaction, and lifecycle facts worth publishing or subscribing to

# Goal

Broaden scene-local event usage only where real gameplay systems benefit from reactive facts.

# What Exists Already

The repo already uses scene-local events for:

- `DamageAppliedEvent`
- `ActorDiedEvent`
- `CubePlacedEvent`
- `CubeDestroyedEvent`
- debug tracing through `EventTraceSystem`

# What Is Missing

- broader world-object lifecycle facts
- interaction-mode facts
- first-class action facts for future audio/VFX/analytics
- richer subscriber examples beyond damage feedback and trace logging

# Recommended Phases

## Phase 1: World-Object Lifecycle Events

Add events such as:

- `WorldObjectRegisteredEvent`
- `WorldObjectRemovedEvent`
- or a narrower destruction/removal fact that future systems can observe

This is most useful once non-cube props exist.

## Phase 2: Interaction Facts

Add first-class scene facts around mode or action transitions where useful, for example:

- `InteractionModeChangedEvent`
- `PrimaryUseTriggeredEvent`

These should only land if a real subscriber needs them.

## Phase 3: Subscriber Growth

Use the new facts for focused systems such as:

- audio reactions
- VFX triggers
- analytics/debug instrumentation
- loot or quest hooks

## Phase 4: Boundary Guardrails

Keep reinforcing the project rule:

- direct ordered simulation stays direct
- events carry facts for cross-system reaction

Do not move movement, physics sequencing, or render pass order into event chains.

# Recommended File-Level Focus

- `dream/PlayScene.cs`
- `dream/SceneEventHub.cs`
- `dream/EventTraceSystem.cs`
- future audio/VFX/quest subscribers as separate focused systems

# Risks

- publishing events for hypothetical future needs
- replacing clear direct sequencing with bus-driven control flow
- bloating `PlayScene` with event boilerplate that has no subscribers

# Recommendation

Widen event use only after the next gameplay slices create real consumers. The best immediate trigger for this roadmap is likely the first enemy actor or the first non-cube world prop.
