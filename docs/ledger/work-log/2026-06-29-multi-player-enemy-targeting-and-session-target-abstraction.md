# Multi Player Enemy Targeting And Session Target Abstraction

Purpose: record the multiplayer foundation slice that replaces single-player enemy targeting assumptions with a shared authoritative session-player target seam.
Scope: shared authoritative player-target contract, enemy aggro/attack/separation over joined sessions, snapshot/action-context cleanup, and focused regression coverage

## Summary

The authoritative runtime no longer treats only the primary `Player` actor as a valid PvE target.

## Completed Work

- Added `IAuthoritativePlayerTarget` plus `AuthoritativePlayerTarget` so shared runtime systems can consume player-session combat/position/facing facts through one focused contract.
- Updated `WorldSessionRuntime` to use that shared target seam for:
  - authoritative player actor lookup
  - player snapshot export
  - action-context resolution
  - joined-player enemy separation
  - enemy target selection inputs
- Updated `EnemyRegistry` and `EnemyActor` so enemies can now:
  - pick the nearest alive spawned authoritative player target
  - chase that target
  - apply melee damage to that target's `PlayerCombatant`
  - stay idle cleanly when no authoritative player target exists
- Extended `RemoteSessionPlayerState` with direct authoritative position setting so collision/separation correction can move secondary joined actors through the same shared target seam.
- Added focused tests covering:
  - nearest authoritative player target selection in `EnemyRegistry`
  - damage landing on a secondary joined session when it is the nearest valid PvE target in `WorldSessionRuntime`

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "EnemyRegistryTests|WorldSessionRuntimeMultiplayerEnemyTests" /p:UseSharedCompilation=false`

## Remaining Limits

- The first joined remote session still aliases the full authoritative `Player` actor while later sessions still use lighter-weight runtime state.
- The desktop client still corrects one locally controlled runtime actor, not arbitrary session actors.
- The broader project-structure cleanup and fuller namespace/module split still remain future work after more multiplayer runtime seams are stabilized.
