# Cube Duplication Root-Cause Fixes

Purpose: Record the fixes for the three duplication mechanisms identified in `docs/ledger/analysis/2026-10-27-cube-physics-render-deep-audit.md` (sections A1–A4).

Status: Implemented and verified by automated tests. Not yet verified in manual play.

## Completed Changes

- **Client mirror no longer simulates placed cubes (A1).** `WorldSessionRuntime` takes `isReplicatedMirror`, and `PlayScene` sets it. The mirror's `Map` passes `replicatedCubeBodies` to `WorldPhysicsBinder`, which creates kinematic, never-sleeping, zero-mass bodies for non-static full cubes and keeps them out of `PhysicsIntegrator.DynamicMapCubeBodies`. `SyncDynamicMapBodies` therefore never moves a mirror cube; cubes move only when `RemoteWorldMutationApplier` applies a server delta. Fracture pieces stay dynamic on the mirror because their motion is not replicated yet.
- **Mirror stops leaking replication state (A1 side effect).** A mirror runtime does not subscribe its replication exports and does not queue `CubeMoved` deltas, so `_pendingWorldMutations` and `_pendingDamageEvents` no longer grow forever on the client.
- **`CubeMoved` never creates a cube (A2).** `ApplyCubeMovedMutation` drops a move whose cube is at neither the source nor the target cell, and logs it as `client.world.move-miss`. It used to `TryAddCube(source)`, which duplicated the client's existing copy and gave the new cube the default Grass material.
- **No backlog replay on join (A3/A4).**
  - `WorldMutationDelta` has a `Sequence`. The authoritative runtime stamps every queued delta through `QueueWorldMutation` and exposes `LastWorldMutationSequence`.
  - `JoinAcceptedMessage.InitialWorldMutationSequence` carries the sequence already reflected in `InitialWorld`. `ClientJoinAcceptedApplier` acknowledges it on the client runtime.
  - `RemoteWorldMutationApplier.ApplySnapshotToRuntime` skips sequenced deltas at or below the acknowledged sequence, and skips sequenced deltas that ship together with a full world snapshot. Unsequenced (legacy) deltas are still applied.
  - `DedicatedServerHost.DiscardUndeliveredSnapshotDeltas` drains pending deltas. `DedicatedServerService.AdvanceOnceAsync` calls it on ticks with no connections or no joined connection, which stops the server-side backlog leak.

## Behavior Change To Watch

On the client, a pushed cube is now a kinematic wall until the server's move delta arrives. Over the integrated host's loopback that is about one tick, but it can make pushing feel firmer, and the server correction moves the local player. Remote clients on real latency will feel it more.

## Verification

- New `dream.Tests/Client/Replication/ReplicatedCubeDuplicationTests.cs` (7 tests). They cover: a move delta with no client copy, mirror cube kinematic and unmoved after 60 ticks, mirror queues no deltas, increasing sequences, a cube placed and fallen before join not duplicated after join plus backlog, deltas after the join baseline still applied, and repeated sequenced deltas skipped.
- Two new `DedicatedServerServiceTests` assert that snapshot deltas are drained when no connection is joined and when no connections exist.
- Full suite: `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`, 595 passed, 0 failed.
- Build: `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false` succeeded with the existing package and analyzer warnings.
- Manual play was not run.

## Follow-Up

- Moves are still resolved by cell, not by a stable cube id. A wrong-cube match is still possible if client and server cells ever disagree.
- No periodic or on-mismatch world resync exists yet.
- Audit items B1–B7 (push performance) and C1 (untextured fallback mesh) are untouched.
