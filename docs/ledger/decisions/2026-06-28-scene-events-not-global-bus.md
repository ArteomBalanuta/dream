# Scene Events Not Global Bus

Purpose: record the durable event-scope rule for `dream`.
Status: accepted
Scope: scene-local event ownership and usage boundaries

## Decision

`dream` uses scene-local event hubs for cross-system facts and reactions instead of introducing a global engine-wide event bus.

## Rationale

- keeps control flow local to the active `PlayScene`
- preserves explicit scene ownership for publication and subscription lifetime
- avoids singleton-style coupling and hidden engine-wide behavior
- keeps event growth tied to real gameplay needs such as combat feedback, death reactions, and optional tracing

## Consequences

- ordered simulation remains direct, including movement, physics stepping, transform sync, and render pass order
- published events should describe facts that already happened, such as `DamageAppliedEvent` or `ActorDiedEvent`
- new reactive systems should subscribe inside scene scope instead of reaching for global infrastructure
- optional diagnostics such as `EventTraceSystem` should stay bounded and opt-in rather than becoming default always-on scene behavior
