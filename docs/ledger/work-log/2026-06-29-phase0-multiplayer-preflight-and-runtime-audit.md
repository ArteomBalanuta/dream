# Phase 0 Multiplayer Preflight And Runtime Audit

Purpose: record the Phase 0 multiplayer preflight seam-hardening verification and documentation pass.
Status: completed
Scope: documentation, work-log, and verification updates for startup seams, runtime ownership notes, and remaining single-player assumptions

## Summary

Recorded the Phase 0 preflight verification results after the startup, interaction, physics-ownership, and low-harness seam work landed.

## Completed Work

- documented that `Program` now performs explicit startup mode selection, keeps desktop as the default path, and reserves `--server` as an honest non-implemented seam
- documented `PlayScene` as the current desktop orchestration root and future extraction boundary for `WorldSessionRuntime`
- documented that `PhysicsIntegrator` no longer constructs the player body implicitly; `PlayScene` composes it with `PhysicsFactory` and registers it explicitly
- documented the request-style local build path where `PlayerInteraction` captures `BuildUseRequest`, `Player` returns it upward, `PlayScene` applies accepted placement, and `Map.AddCube(...)` returns `bool`
- documented the focused low-harness regression coverage around `PlayerController`, `PlayerInteraction`, `PlayerMotor`, and `PhysicsIntegrator` ownership behavior

## Verification

- `dotnet build dream\\dream.csproj --nologo` passed
- expected warnings remain, including `NU1701` for `BulletSharp 0.11.1`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo` passed with 103 tests
- `dotnet run --project dream\\dream.csproj -- --server` exits with code 1 and prints: `The --server startup path is not implemented yet. This phase only reserves the startup seam, so no headless host or transport bootstrap runs yet.`
- controlled desktop startup check: launched `dotnet run --project dream\\dream.csproj`, observed the process stayed alive for 5 seconds, then stopped it intentionally (`STARTED_AND_STOPPED_AFTER_5S`)

## Rechecked Desktop Behavior

- the default no-argument desktop startup path still boots instead of being displaced by the new mode-selection seam
- the scene-owned local runtime path still stays alive long enough to enter the active desktop loop after the seam-hardening changes
- the reserved `--server` path still fails fast and honestly instead of attempting partial host or transport bootstrap work

## Remaining One-Player Assumptions Before Phase 1

- `PlayScene` still owns authoritative world/player/physics update order for the desktop path
- `WorldSessionRuntime` does not exist yet
- there is still one locally composed `Player` and one registered player body in the active runtime path
- `--server` is only a reserved seam, not a working host
- no transport, session registry, or remote-player path exists yet
