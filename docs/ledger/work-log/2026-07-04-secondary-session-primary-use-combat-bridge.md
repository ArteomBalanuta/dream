# 2026-07-04 Secondary Session Primary-Use Command Bridge

Purpose: record the fix for joined clients being unable to execute server-owned primary-use actions through command-driven input.

## Summary

Joined clients could move, face targets, and switch modes, but their command-driven primary use never became an authoritative action on the server tick path.

The host-controlled path already resolved primary use inside `WorldSessionRuntime.UpdateAcceptedCommand(...)`. Secondary sessions only advanced movement/facing through `AdvanceSecondaryRemoteSession(...)`, and `RuntimeAuthoritativeSessionHost.AdvanceTick(...)` never synthesized the matching action from a `PrimaryUsePressed` command.

## What Changed

- Added a regression test proving that a secondary remote session can damage an enemy using only a tick-driven `PlayerCommandFrame` with `PrimaryUsePressed = true`.
- Added a regression test proving that the same secondary command path still places cubes when the authoritative interaction mode is Building.
- Added `WorldSessionRuntime.TryCreateCommandPrimaryUseActionMessage(...)` so command-driven primary use is translated once from authoritative session context instead of local `Player.Camera` state.
- Updated `RuntimeAuthoritativeSessionHost.AdvanceTick(...)` to synthesize secondary-session authoritative actions after advancing that session's authoritative movement/facing state.
- Kept the action authoritative on the server so world mutations and enemy health changes still flow through normal action acceptance, cooldown, mutation, and damage-result replication.

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~AdvanceTick_SecondaryRemotePrimaryUseCombat_DamagesEnemy|FullyQualifiedName~AdvanceTick_SecondaryRemotePrimaryUseBuilding_PlacesCube"`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~RuntimeAuthoritativeSessionHostTests|FullyQualifiedName~WorldSessionRuntimeCombatEventQueueTests|FullyQualifiedName~AuthoritativeActionFlowTests|FullyQualifiedName~DedicatedServerHostTickTests"`
- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`
