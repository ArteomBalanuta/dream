Purpose: Record the completed first-pass wolf enemy slice and its verification.
Status: Completed.
Scope: Wolf asset import, enemy runtime, enemy targeting, melee combat exchange, world rendering, and related documentation

# Summary

Implemented the first non-player combat actor for `dream` as a repo-owned animated wolf asset.

The completed slice adds:

- `dream/animations/enemies/wolf.glb` as the first enemy model
- `WolfAssetDefinition` plus `WolfAnimationResolver` for explicit enemy asset metadata
- `EnemyActor`, `EnemyCombatant`, `EnemyRegistry`, and `EnemyVisualState` for focused enemy runtime behavior
- `EnemyRenderer` and `EnemyRenderState` for the first dedicated world-space animated-actor render path
- `PlayScene` integration for wolf spawn, chase behavior, melee attack cadence, player damage, enemy hit targeting, and death pruning
- tests covering enemy asset paths, combatant cooldown/death behavior, state transitions, hit resolution, and render-state composition

# Notes

- The current pass is intentionally a single-wolf vertical slice.
- Enemy movement is direct chase math with no pathfinding or collision body yet.
- Enemy targeting is resolved from the camera ray before destructible cube targeting while in combat mode.
- Enemy updates pause while the settings menu is open because the scene already pauses player interaction there.

# Verification

Ran:

- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`

Observed:

- build passed with the known `BulletSharp` restore warning
- tests passed: `141` total, `0` failed
