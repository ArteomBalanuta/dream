# Phase 1 World Session Runtime Extraction

Date: 2026-06-29
Status: completed for the current desktop-backed seam

## Summary

Phase 1 extracted the first authoritative `WorldSessionRuntime` seam from `PlayScene` so the desktop client path no longer owns the full gameplay update order directly.

## Completed Work

- Created `WorldSessionRuntime` as the authoritative desktop-session root for world state, physics ownership, player ownership, enemy upkeep, combat/build resolution, and floor rebuild behavior.
- Moved authoritative `Map` and `PhysicsIntegrator` creation behind the runtime seam.
- Moved authoritative player update orchestration behind the runtime seam while keeping desktop input capture in `PlayScene`.
- Replaced raw desktop runtime dependencies in the authoritative update path with smaller command/tuning records such as `PlayerSimulationInput` and `PlayerSimulationTuning`.
- Added `WorldSessionPresentationState` so `PlayScene.Render()` now consumes a presentation-facing runtime snapshot instead of reading several runtime-owned objects field by field.
- Kept render, HUD, debug draw, viewmodel animation, and local combat-feedback presentation client-local.

## Verification

- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Passed with the existing `BulletSharp` compatibility warning and existing project warnings.
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Reported `185` tests passed.
  - The test host still aborts afterwards because of the known `BulletSharp` / `libbulletc` finalizer crash.
- Controlled desktop startup:
  - `dotnet run --project dream\\dream.csproj --no-build`
  - The client stayed alive for an 8-second controlled run and was then terminated intentionally.

## Current Limitations

- The runtime seam is still desktop-backed and single-session.
- No transport, session registry, dedicated server host, or remote-player replication path exists yet.
- `WorldSessionRuntime` is cleaner and more host-agnostic than before, but it is not yet sufficient on its own to run a dedicated authoritative server process.
