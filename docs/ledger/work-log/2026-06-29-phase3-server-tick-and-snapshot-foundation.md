# Phase 3 Server Tick And Snapshot Foundation

Date: 2026-06-29
Status: completed for the current in-process desktop transition path

## Summary

Phase 3 established the first explicit 64 Hz authoritative tick contract and the first transport-neutral snapshot/delta export layer needed by later dedicated-server phases.

## Completed Work

- Added `SimulationClock` and `ServerTickRunner` as the first fixed-step 64 Hz scheduling seam.
- Updated the desktop client transition path so `PlayScene` now advances `WorldSessionRuntime` through fixed authoritative ticks instead of directly coupling simulation cadence to render-frame delta.
- Added transport-neutral replication DTOs: `ActorSnapshot`, `WorldSnapshot`, `WorldMutationDelta`, `DamageEventDelta`, and `ServerSnapshotEnvelope`.
- Added authoritative snapshot export from `WorldSessionRuntime`, including current server tick, accepted per-session command sequence metadata, authoritative actor state, optional world snapshot data, and pending world/combat deltas.
- Added runtime-side subscriptions that translate scene-local `CubePlaced`, `CubeDestroyed`, `DamageApplied`, and `ActorDied` facts into explicit pending export deltas without changing the local feedback/event path.

## Verification

- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Passed with the existing `BulletSharp` compatibility warning and existing project warnings.
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "SimulationClockTests|ServerTickRunnerTests"`
  - Passed for the new fixed-tick coverage.
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Reported `190` tests passed.
  - The test host still aborts afterwards because of the known `BulletSharp` / `libbulletc` finalizer crash.
- Snapshot export verification:
  - Verified only in-process through build/runtime wiring and DTO integration in this phase.
  - No real server host path exists yet, so no networked snapshot smoke test was possible.
- Controlled desktop startup:
  - `dotnet run --project dream\\dream.csproj --no-build`
  - The client stayed alive for an 8-second controlled run and was then terminated intentionally.

## Current Limitations

- The authoritative tick path is still driven only by the local desktop host during transition.
- The snapshot/delta export layer exists, but no dedicated server host or transport wiring consumes it yet.
- Snapshot export tests that instantiate the full Bullet-backed runtime were intentionally not kept because this environment still lacks stable native `libbulletc` availability for that style of focused runtime test.
