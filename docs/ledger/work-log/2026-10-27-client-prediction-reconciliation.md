# Client Prediction Reconciliation

Purpose: Record the fix for section 2 of `docs/ledger/analysis/2026-10-27-movement-jitter-and-push-freeze.md`: the local player was hard-snapped to every snapshot, which caused jitter at speed.

Status: Implemented and verified by automated tests. Not yet verified in manual play.

## Completed Changes

- **Reconciler.** Added `Client/Replication/ClientPredictionReconciler`.
  - `RecordPrediction(sequence, position)` stores the predicted position after each local tick, keyed by the command sequence that tick simulated. The stored position includes any pending correction.
  - `Reconcile(ack, serverPosition)` compares the server position with the latest sample at or before the acknowledged sequence:
    - **None:** an error of 5 cm or less.
    - **Smooth:** an error under 3 m. It is added to a pending offset, and the remaining history is shifted by the same error so the next snapshot doesn't count it again.
    - **Snap:** an error of 3 m or more, or no acknowledged command. History is cleared.
    - **Wait (reported as None):** every sample is newer than the ack, for example right after a snap. This avoids a snap loop.
  - `TakeSmoothingStep(dt)` drains the pending offset linearly, so it finishes exactly within 0.1 s.
- **Gentle moves.** Added `Player.NudgeWorldPosition` and `PlayerCameraRig.Translate`. They move the body and camera without zeroing velocity.
- **Applier.** `ClientCorrectionApplier.ApplySnapshotToRuntime` takes an optional reconciler. With none, it still hard-snaps, which the join bootstrap uses. With one, it reads `LastAcceptedCommandSequences[localSession]` and snaps only when the reconciler says so.
- **`PlayScene` wiring.**
  - Owns one reconciler and passes it for snapshots.
  - Applies one smoothing step per frame after draining server messages.
  - Records a prediction after each local tick.
  - Resets the reconciler on join and on rejected-action position corrections.
- **Shared movement tuning.** Added `PlayerSimulationTuning.AuthoritativeDefault` (move 6, noclip 18, jump 5). `DedicatedServerHost` now uses it. Client prediction uses `WithAuthoritativeMovement()`, which keeps local eye height and mouse sensitivity.
  - Before this, the client predicted with its own `NoclipSpeed` (settable up to 300) while the server always used 18, so fast noclip disagreed with the server on every snapshot.

## Behavior Change

The local `NoclipSpeed` setting no longer affects connected movement. The server never honored it; it only caused rubber-banding. Making it real needs server-side per-player tuning.

## Verification

- New `dream.Tests/Client/Replication/ClientPredictionReconcilerTests.cs` (11 tests):
  - no correction when the server trails by latency but matches the acknowledged prediction;
  - a small error smooths and is not applied twice;
  - smoothing drains completely within its window;
  - samples recorded mid-smoothing include the pending offset;
  - a large error snaps and clears history;
  - after a snap, the reconciler waits instead of snapping again;
  - a snapshot with no acknowledged command snaps;
  - the applier keeps position and velocity on a matching snapshot;
  - the applier snaps on a large divergence;
  - `NudgeWorldPosition` keeps velocity and moves the camera;
  - `WithAuthoritativeMovement` keeps view settings and takes server movement.
- Full suite: `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`, 630 passed, 0 failed.
- Build: `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false` succeeded with the existing package and analyzer warnings.
- Not verified in manual play.

## Known Limits

- The server applies the latest command per tick, and the client may simulate a different number of ticks with the same command. Each such tick mismatch adds an error of about speed × 1/64 s (about 9 cm walking, 28 cm in noclip). That is smoothed, not snapped.
- Correction is position-only. There is no input replay, and remote-player interpolation is unchanged.
