Purpose: record the remote multiplayer fix for client-local camera-relative movement presentation and authoritative damage feedback rehydration.
Status: completed
Scope: local simulation command policy, snapshot damage-event intake, regression coverage, and verification notes

# Summary

Two remote-mode presentation regressions were fixed:

- local movement stopped respecting the direction of the client camera
- accepted combat hits could update authoritative state without showing floating damage text on the client

# Root Cause

- `ClientSimulationCommandPolicy` stripped `MoveIntent` completely when remote authority was enabled. The client still applied immediate look locally, but the in-process runtime never received movement intent, so local presentation could no longer move relative to the client camera.
- Remote primary-use combat processed inside the server tick path contributes `DamageEventDelta` entries to snapshot replication, but the client did not turn those authoritative snapshot damage entries back into local `DamageAppliedEvent` feedback facts.

# Changes

- Updated `ClientSimulationCommandPolicy` so remote-mode local simulation now preserves `MoveIntent` while still stripping authoritative look replay and one-shot primary-use execution.
- Added `ClientSnapshotDamageApplier` so snapshot `DamageEventDelta` entries can publish local `DamageAppliedEvent` feedback once the target actor is resolvable in the client runtime.
- Updated `PlayScene.ApplyReplicatedMessage(...)` so remote snapshot application now:
  - applies world and correction state
  - mirrors authoritative enemies
  - rehydrates snapshot damage feedback into the local event-driven floating-text path

# Verification

- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`

# Notes

- The build and tests still report the known `NU1701` warning for `BulletSharp`.
- Snapshot damage rehydration currently restores regular floating-text feedback for authoritative hit events. If critical-hit styling also needs to round-trip through snapshot-only combat paths, `DamageEventDelta` will need an explicit floating-text kind field in a future pass.
