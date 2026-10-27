# Fracture Ownership Persistence Crash Fix

Purpose: Record the implementation that stops authoritative cube-fracture persistence from killing the integrated server loop after registry identity drift.

Status: Implemented.

Scope: Runtime combat fracture flow, cube-record identity repair, persistence-owner validation, and regression coverage.

## Problem Fixed

The live diagnostics run in `logs/manual-play-20260705-194309/` showed that combat could stop working because the server tick loop died after a fatal destructible-cube hit.

The failure path was:

- `ProcessCombatUseAction`
- `PersistFracturePieces`
- `SaveCubeFracturePiece`

When a visible moved cube had lost its registry link, combat could resolve it through a fresh anonymous `WorldObjectRecord`. That new record had a new actor id and no valid owner. On fatal fracture, persistence then either:

- failed to delete the original placed-cube row
- attempted to save fracture pieces with invalid ownership

That state matched the observed "damage stops after a while" symptom because the authoritative server loop died and the client kept sending actions into a dead authority path.

## Changes

- Added a ledger plan for the slice in [2026-07-05-fracture-ownership-persistence-crash-fix.md](/G:/workspace/projects/dream/docs/ledger/plans/2026-07-05-fracture-ownership-persistence-crash-fix.md).
- Added `WorldObjectRegistry.ReplaceCubeRecordIdentity(...)` so a visible cube can be rebound to its original actor id, health, and owner when persistence proves the authoritative lineage.
- Added runtime repair in `WorldSessionRuntime` so destructible cube hits repair anonymous or drifted records from persisted placed-cube identity before damage and persistence run.
- Updated placed-cube and fracture-piece persistence to resolve a validated owner and authoritative cube id instead of falling back to `"unknown-player"`.
- Added a regression test that reproduces:
  - moved placed cube
  - missing registry link
  - fatal combat hit
  - fracture persistence keeping the original cube id and owner

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~ProcessAction_MovedPlacedCube_" --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~RuntimeAuthoritativeSessionHostTests|FullyQualifiedName~CombatUseResolverTests|FullyQualifiedName~SqlitePersistenceRepositoryTests" --nologo /p:UseSharedCompilation=false`

Full-suite and build verification were run after the implementation as well.

## Result

This slice closes the specific server-death path from the captured diagnostics session:

- moved/rebound cubes remain damageable
- fracture persistence keeps the original player owner and cube id
- the fix avoids writing invalid fracture-piece ownership into SQLite-backed authoritative runs
