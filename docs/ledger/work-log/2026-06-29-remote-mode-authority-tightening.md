# Remote Mode Authority Tightening

Purpose: record the next reconciliation slice for remote desktop mode so local runtime behavior interferes less with authoritative server state.
Status: complete
Scope: remote-mode local authority suppression, enemy snapshot mirroring, and broader authoritative client-state intake

## Summary

Remote mode now gives the server more room to stay in charge.

- Added `RemoteEnemyStateApplier` so authoritative enemy actor snapshots can replace client-local enemy runtime state.
- Added `EnemyRegistry.Clear()` and `EnemyActor.SetFacing(...)` to support snapshot-driven enemy replacement.
- Updated `PlayScene.ApplyReplicatedMessage(...)` so remote mode now mirrors:
  - local player authoritative position/facing/HP
  - world snapshot and world mutation deltas
  - enemy snapshot state
- Updated remote-mode tick behavior in `PlayScene.Update(...)` so when a remote session is connected:
  - local command frames are still sent to the server
  - local runtime primary-use authority is suppressed
  - local enemy gameplay simulation is suppressed

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "RemoteEnemyStateApplierTests|ClientAuthoritativeStateApplierTests|RemoteClientSessionTests" /p:UseSharedCompilation=false`
  - Result: passed, `8` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with the known `BulletSharp` restore warning and existing project warnings
- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Result: passed, `228` tests

## Remaining Gaps

- Remote mode still keeps the local runtime alive for camera and presentation continuity, so player movement and look are not yet fully server-driven.
- Remote combat feedback is still not fully sourced from authoritative damage/correction ownership.
- The server still supports only one remote-controlled joined player session inside the authoritative runtime.
