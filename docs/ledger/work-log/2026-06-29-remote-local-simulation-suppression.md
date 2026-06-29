# Remote Local Simulation Suppression

Purpose: record the next remote-mode authority step so connected desktop clients stop simulating local movement and look against authoritative snapshots.
Status: complete
Scope: remote-mode local simulation command policy, focused regression coverage, and documentation alignment

## Summary

Remote desktop mode now strips local simulation down to the pieces we still need client-side immediately.

- Added `ClientSimulationCommandPolicy` so local runtime simulation can choose between:
  - the full captured `PlayerCommandFrame` for local/offline play
  - a remote-authority simulation frame that preserves session, sequence, and requested mode but suppresses movement, look, and primary use
- Updated `PlayScene.Update(...)` to route local runtime advancement through that policy before calling `_runtime.AdvanceAuthoritativeTick(...)`.
- Kept outbound transport behavior unchanged, so the full captured command frame still goes to the server while the local runtime stays passive in remote mode.
- Added focused tests that lock in both policy modes.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter ClientSimulationCommandPolicyTests /p:UseSharedCompilation=false`
  - Result: passed, `2` tests

## Remaining Gaps

- Remote mode still keeps the in-process runtime alive for camera, HUD, and viewmodel continuity.
- Remote combat feedback and broader prediction/rollback ownership still need later cleanup.
- The current runtime still supports only one remote-controlled joined player session even though multiple TCP clients can connect.
