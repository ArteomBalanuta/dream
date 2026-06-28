# Reactive System Expansion

Purpose: record the first expansion of scene-local events beyond the initial combat-feedback slice.
Status: completed
Scope: combat-layer death events, optional trace instrumentation, and durable event-scope documentation

## Summary

Expanded the scene-local reactive model with additional combat facts and opt-in subscriber examples while preserving the rule that ordered simulation remains direct.

## Completed Work

- added `ActorDiedEvent` publication support in the combat layer through `PlayerCombatant` so fatal damage can emit a scene-local domain fact
- added `EventTraceSystem` as a bounded scene-local subscriber example for debug or telemetry use without wiring it into `PlayScene` by default
- kept the reactive slice aligned with the earlier combat-feedback decoupling so `DamageAppliedEvent` and `ActorDiedEvent` can support future audio, VFX, creature, or breakable reactions
- updated architecture guidance to state that scene-local events are for cross-system facts and reactions, while ordered simulation remains explicit
- recorded the durable decision to prefer scene-local hubs over a global engine-wide bus

## Verification

- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
