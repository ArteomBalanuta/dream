# Phase 2 Player Session And Command Frames

Date: 2026-06-29
Status: completed for the current local desktop path

## Summary

Phase 2 replaced the implicit one-local-player input shortcut with explicit player session identity and transport-safe command frames while preserving the current desktop play path.

## Completed Work

- Added `PlayerSessionId`, `ConnectedPlayer`, and `PlayerSessionRegistry` as the first runtime session-ownership seam.
- Registered the current local player under a stable local session identity inside `WorldSessionRuntime`.
- Added transport-safe command DTOs: `PlayerCommandFrame`, `PlayerMoveIntent`, `PlayerLookIntent`, and `PlayerModeSwitchIntent`.
- Updated `PlayScene` to capture one local command frame per update and route it into the runtime with a monotonic sequence number.
- Updated `WorldSessionRuntime` to validate command sequence monotonicity through `PlayerSessionRegistry` before consuming command intent.
- Kept the current player simulation path intact by translating accepted command frames into the existing runtime-facing movement/look/use flow.

## Verification

- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Passed with the existing `BulletSharp` compatibility warning and existing project warnings.
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "PlayerSessionRegistryTests|PlayerControllerTests"`
  - Passed for the new session/command-frame coverage.
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Reported `189` tests passed.
  - The test host still aborts afterwards because of the known `BulletSharp` / `libbulletc` finalizer crash.
- Controlled desktop startup:
  - `dotnet run --project dream\\dream.csproj --no-build`
  - The client stayed alive for an 8-second controlled run and was then terminated intentionally.

## Current Limitations

- The session registry currently tracks one local player path only.
- `PlayerCommandFrame` is transport-safe, but no actual transport, dedicated server loop, or remote-session routing exists yet.
- The runtime still updates one authoritative `Player` instance; multi-player actor simulation and replication remain future phases.
