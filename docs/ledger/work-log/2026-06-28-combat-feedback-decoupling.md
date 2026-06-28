# Combat Feedback Decoupling

Purpose: record the first gameplay-to-presentation decoupling slice using scene-local events.
Status: completed
Scope: floating combat text moved behind `DamageAppliedEvent`

## Summary

Removed the direct `PlayScene` to `HudRenderer` floating-damage dependency by routing combat feedback through a local scene event subscription owned and wired by `PlayScene`.

## Completed Work

- added production wiring so `PlayScene` now constructs and updates `CombatFeedbackSystem`
- corrected the seam so `PlayScene` constructs `CombatFeedbackSystem` directly instead of asking `HudRenderer` to create it
- replaced direct HUD damage spawning with `DamageAppliedEvent` publication from gameplay orchestration
- kept `HudRenderer` and `Render` on presentation responsibility by passing `CombatFeedbackSystem` in as render input
- updated architecture notes to describe the event-driven combat feedback flow

## Verification

- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
