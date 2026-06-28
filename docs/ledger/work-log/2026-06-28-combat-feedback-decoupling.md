# Combat Feedback Decoupling

Purpose: record the first gameplay-to-presentation decoupling slice using scene-local events.
Status: completed
Scope: floating combat text moved behind `DamageAppliedEvent`

## Summary

Removed the direct `PlayScene` to `HudRenderer` floating-damage dependency by routing combat feedback through a local scene event subscription.

## Completed Work

- added production wiring so `PlayScene` now constructs and updates `CombatFeedbackSystem`
- replaced direct HUD damage spawning with `DamageAppliedEvent` publication from gameplay orchestration
- kept `HudRenderer` on presentation responsibility by removing public gameplay-triggered damage spawn entry points
- updated architecture notes to describe the event-driven combat feedback flow

## Verification

- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
