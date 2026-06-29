# Authoritative Client State Mirroring

Purpose: record the first client-side application of authoritative local-player and world state in remote mode.
Status: complete
Scope: local player snapshot correction, world snapshot/mutation reconciliation, and remote-client runtime intake

## Summary

Remote desktop mode now consumes more than transport metadata and remote-player presentation.

- Added `IReplicatedPlayerTarget` so local-player authoritative application depends on a narrow target seam rather than directly on the full runtime.
- Updated `Player` to implement that replicated-player target seam.
- Added `ClientCorrectionApplier` so authoritative local-player snapshots can now:
  - reposition the local player
  - update facing
  - sync current/max HP
- Added `RemoteWorldMutationApplier` so authoritative world state can now:
  - reconcile a full placed-cube snapshot against the current client map
  - apply incremental cube placement/destruction deltas
- Updated `PlayScene.ApplyReplicatedMessage(...)` so remote mode applies these authoritative world/player updates before feeding snapshot data into client presentation buffering.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "ClientAuthoritativeStateApplierTests|RemoteClientSessionTests" /p:UseSharedCompilation=false`
  - Result: passed, `6` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with the known `BulletSharp` restore warning and existing project warnings
- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Result: passed, `226` tests

## Remaining Gaps

- Remote mode still keeps the local runtime simulating, so authoritative state mirroring can still compete visually with local assumptions until stronger reconciliation lands.
- Enemy and broader combat/world state are not yet fully mirrored into the client runtime from authoritative snapshots.
- The server still supports only one remote-controlled joined player session inside the authoritative runtime.
