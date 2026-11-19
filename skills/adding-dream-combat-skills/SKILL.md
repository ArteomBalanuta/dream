---
name: adding-dream-combat-skills
description: Adds or changes a Dream player combat skill end to end - the skills.json row, a shared tick timeline, server-authoritative resolution, a skeletal clip in gen_char_player.py, the cast effect, the quickbar icon - using Warrior Cleave as the worked reference. Use when adding a new active skill, retuning a skill's cost, cooldown, arc, timing or damage, making a skill animation faster or more visible, or when a clip, effect and hit window drift out of step.
---

# Adding Dream Combat Skills

Cleave (`skill.cleave`, kind `MeleeArc`) is the reference implementation. Read its plans before designing:
`docs/ledger/plans/2026-11-19-cleave.md` (timeline, server swing, clip, crescent, icon) and
`docs/ledger/plans/2026-11-19-cleave-trail.md` (the white blade trail, faster timing). Sibling skills you will need:
**`adding-dream-effects`** (particle cells and spawn paths), **`generating-dream-assets`** (generator gates, icons,
clips), **`capturing-dream-screenshots`** (see it in the engine).

## The one rule

**One timeline, defined once in C#, read by everyone.** `dream/Simulation/Combat/MeleeArcSwing.cs` holds the tick
counts (wind-up 8, active 6, recovery 16 at 64 ticks/s) and derives every seconds value from them. The server hit
window, `PlayerAnimationStateMachine.CleaveSeconds`, the effect delay and the clip keyframes all read it. Python
generators may not import C#, so `gen_char_player.py` repeats the numbers and a C# test
(`dream.Tests/Rendering/Actors/PlayerCleaveClipTests.cs`) pins the shipped clip to the constants. Change a tick
count and that test tells you to regenerate the GLB. Never retune one side by hand.

## The layers, in order

1. **Content.** `dream/config/content/skills.json`: `key`, `kind` (`SkillKind`: `MeleeArc`, `RangedBolt`,
   `SelfHeal`), `manaCost`, `cooldownTicks`, `range`, `arcDegrees`, `damagePercent`, `iconKey`; `defaultQuickbar`
   places it. Validated by `ContentCatalog` and `dream --validate-content` (exit 0/1/8). A MeleeArc with no
   `maxTargets` hits every target (`SkillDefinition.AllTargets`). A new `SkillKind` is a server change, not content.
2. **Server.** `WorldSessionRuntime.Skills.cs` accepts the cast (mana, cooldown, spawn protection) and, for MeleeArc,
   queues a pending swing in `WorldSessionRuntime.MeleeSwings.cs`. That resolves the cone through
   `AuthoritativeCombatResolver.ResolveMeleeArcSweep` on every tick of the active window, with a per-cast hit set
   (one hit per target) and re-checks that the caster is still spawned, alive and in Combat mode. Swings are pooled:
   `MeleeSwingAllocationTests` requires zero steady-state allocations. No protocol change was needed (still v22); a protocol bump
   touches several places (see `docs/architecture.md`, Client session lifecycle).
3. **Animation.** `tools/ui-assets/gen_char_player.py` `clip_cleave` writes the `cleave` clip into
   `dream/animations/players/player-grim.glb`. `SkillSwingSerial` on `PlayerAnimationInputs` / `LocalPlayerFrameInputs` /
   `RemotePlayerPresentationState` starts it: the local player's accepted `AbilityUsedResult`
   (`PlayScene.Replication.cs`) or a remote `CastEventDelta` (`ClientScenePresenter.cs`) bumps the serial.
   A cut must read as fast: short wind-up, a sweep over a few ticks, a long recovery.
4. **Effect.** `FxEventBridge` -> `FxSimulation`. The crescent (`FxArcGeometry`) has its outer edge on the hit
   range; the blade trail (`FxBladeTrailGeometry`, `FxInstance.Trail` frame 31) lays additive white segments along
   the tip path. Positions are in world metres: authored model metres times
   `FxSimulation.WorldMetresPerAuthoredMetre` (about 0.63). Painted cells are measured against the C# geometry by
   `FxArcGeometryTests` and `FxBladeTrailCellTests`.
5. **Icon.** A sprite named after `iconKey` in `tools/ui-assets/gen_items.py` (`icon_skill_cleave`); the quickbar
   resolves it (`QuickbarCleaveIconTests`).

## Verify

- `tools\dev\test.ps1 -Area Simulation`, then `-Area Rendering`, then `-Area Full` before handing off.
- `python -I tools\ui-assets\tests\check_char.py`, `check_particles.py`, `check_items.py` after regenerating.
- Look at it: `capture.ps1 -FromSource` with `dev:clear;spawn:wolf:3:3` to stage targets, then a cast and several
  `snap` steps across the swing (see `capturing-dream-screenshots`). A unit test cannot tell you a trail sits above
  the head or a sword points at the sky; a capture can.
- `tools\golden\run.ps1 -Scene all` catches a regression in the staged golden scenes.
