# Engine Iteration Master Plan: Hot Paths, GL Ownership, Determinism, Decoupling, Wire

Purpose: Split one iteration of foundation work into many independent tasks that implementers can run in parallel waves.
Status: Completed. Waves 1–6 are integrated and verified on `integrate/engine-iteration`. Work continues in `2026-10-28-engine-iteration-round-2-plan.md`.
Scope: Voxel meshing and render export, GL resource ownership and threading, the deterministic authoritative simulation, simulation/presentation decoupling, the wire protocol and server I/O.

## How This Plan Was Made

- **Specialist audits.** Memory, graphics and threading, and netcode audits picked the single highest-priority gap: the presentation render export was rebuilt every frame, about 1.9 MB per frame. Task A fixed that (`2026-10-27-static-render-export-caching.md`).
- **Analysts.** At the user's request ("split by task, more analysts"), five analysts each produced a task list:
  - memory and GC on frame and tick paths (MEM);
  - GL resources and threading (GFX);
  - determinism and interpolation (NET);
  - simulation/presentation decoupling (DEC);
  - wire protocol and server I/O (WIRE).
- **Merging.** This plan merges their lists into waves. Tasks in one wave never edit the same file. A task that needs a file another task in the same wave owns moves to a later wave.

## Rules For Every Task

- **Base.** Branch `iteration/engine-base`: task A's commit `5fd40d0` on `fix/cube-sync-and-architecture-review`, plus the shared `dream.Tests/Utilities/AllocationAssert.cs`. Agent worktrees are created from `master`, so each implementer first runs `git reset --hard iteration/engine-base` on its own empty branch.
- **Scope.** Edit only the files the task owns, plus new test files named for the task. Stop and report if another file is needed.
- **Standards.**
  - Hot paths allocate nothing in steady state: no LINQ, closures or iterators per frame or tick. Use `Span<T>`, `stackalloc` or `ArrayPool` where they fit.
  - GL calls happen only on the render thread, and every GL owner has an idempotent `Dispose`.
  - Heavy CPU work runs off the render thread.
  - The simulation is deterministic and does not depend on presentation code.
- **Wire format.** It stays byte-identical unless the task bumps the protocol version.
- **Verification.**
  - Build: `MSYS_NO_PATHCONV=1 dotnet build dream/dream.csproj` once in the new worktree. The test build copies Bullet natives from `dream/bin`.
  - Tests: `MSYS_NO_PATHCONV=1 dotnet test dream.Tests/dream.Tests.csproj "/p:BaseOutputPath=<worktree absolute path>/artifacts/verify-bin/"`.
  - Never stop a running `dream.exe`.
  - Baseline: 694 tests pass on `iteration/engine-base`.
- **Allocation claims.** Each one gets a before and after measurement with `AllocationAssert`.

## Wave 1 (parallel, disjoint files)

| Id | Task | Owns |
| --- | --- | --- |
| GFX-1 | `Shader` becomes a sealed `IDisposable` with no finalizer. Failed compiles do not leak. Render-thread affinity helper and debug assert. | `Rendering/Core/Shader.cs`, `Rendering/Core/GLUtils.cs`, `Application/Game.cs` |
| GFX-2 | Idempotent disposal for `AnimatedModel` and `StartupMenuRenderer`. `ResourceCache.DisposeAll` and `ModelAssets.DisposeAll`. | `Rendering/Models/AnimatedModel.cs`, `Rendering/Core/ResourceCache.cs`, `Rendering/Core/ModelAssets.cs`, `Client/Startup/StartupMenuRenderer.cs` |
| GFX-5/6 | `BackgroundWork.TryTakeCompleted`/`ObserveAndForget` and `RenderFaultLog`. Static chunk worker faults are reported and retried with backoff instead of crashing `Draw`. | new `Rendering/Core/BackgroundWork.cs`, new `Rendering/Core/RenderFaultLog.cs`, `Rendering/World/StaticWorldChunkRenderer.cs` |
| WIRE-1 | Allocation-free `Vector3JsonConverter.Read`. | `Multiplayer/Protocol/Vector3JsonConverter.cs` |
| WIRE-2 | Zero-copy deframing: span frame bounds, span reads on both receive loops. | `Multiplayer/Protocol/IMessageFramer.cs`, `LengthPrefixedMessageFramer.cs`, `FrameReceiveBuffer.cs`, `Server/ServerConnection.cs` (inbound), `Client/Networking/RemoteClientSession.cs` (inbound) |
| WIRE-3 | Single-pass decode with `Utf8JsonReader` and a payload checkpoint. | `Multiplayer/Protocol/TransportJsonCodec.cs` |
| NET-2 | `DeterministicRandom` (SplitMix64, `Seed.Combine`, `StableHash`). Fracture rubble is seeded by cell, so the server and the client fallback match. The client fallback keeps the cell material and logs when it runs. | new `Simulation/Determinism/DeterministicRandom.cs`, `Simulation/World/Geometry/CubeFracturePattern.cs`, `Client/Replication/RemoteWorldMutationApplier.cs` |
| NET-3 | Server-tick clock and an interpolation buffer keyed by tick for remote players. This replaces receive-time interpolation. | new `Client/Presentation/ServerTickClock.cs`, `RemotePlayerInterpolationBuffer.cs`, `RemotePlayerSnapshotSample.cs`, `ClientScenePresenter.cs`, `Client/Scenes/PlayScene.cs` |
| DEC-1 | Move `FloatingCombatTextKind` to `Simulation.Combat`. Folder-parameterised `DependencyDirectionTests`, with Combat, Inventory and Physics asserted. | the enum (moved), its Simulation, Client, Rendering and test users, `EnemyActor.cs` (using only), `DependencyDirectionTests.cs` |
| DEC-2 | Remove windowing and client input from `Player`, `Camera` and `RemoteSessionPlayerState`. `PlayerMovementResolver.CreateMovementCommand`. `IReplicatedPlayerTarget` moves to Simulation. | `Simulation/Players/Player.cs`, `Camera.cs`, `PlayerMovementResolver.cs`, `Simulation/Runtime/RemoteSessionPlayerState.cs`, `Client/Input/PlayerController.cs`, `Client/World/ClientWorldSession.cs`, `IReplicatedPlayerTarget` (moved) |
| DEC-4 | The presentation surface leaves the authoritative runtime. `WorldSessionPresentationState` moves to `Client.Presentation`. | `Simulation/Runtime/WorldSessionRuntime.cs`, `WorldSessionPresentationState` (moved), `Rendering/World/WorldRenderer.cs` (using only) |
| DEC-5 | Cube visual models leave the simulation (`Cube.Animation` and the descriptor's `Animation`). A renderer-side `CubeMaterialModelCatalog` replaces them. | `Simulation/World/Cube.cs`, `Geometry/GeometryRenderBlockDescriptor.cs`, `WorldGenerator.cs`, `Map.cs` (only if it builds descriptors), new `Rendering/World/CubeMaterialModelCatalog.cs`, `WorldRenderBatchPlanner.cs`, `Rendering/Core/Render.cs` |
| MEM-2 | `PhysicsSyncSystem` with no steady-state allocations. | `Simulation/Physics/PhysicsSyncSystem.cs` |
| MEM-4 | Server host tick pipeline with no allocations. | `Simulation/Runtime/ServerTickRunner.cs`, `Server/DedicatedServerHost.cs`, `Server/DedicatedServerService.cs`, `Multiplayer/Sessions/LatestPlayerCommandInbox.cs`, `PlayerActionQueue.cs`, `Simulation/Runtime/RuntimeAuthoritativeSessionHost.cs` |
| MEM-8 | Chunk mesh build without per-face arrays or iterators. Zero-copy upload. | `Rendering/World/StaticWorldChunkMeshBuilder.cs`, `StaticWorldChunkMesh.cs`, `Rendering/Core/StaticMeshBuffer.cs` |

## Later Waves (planned; re-checked after each integration)

- **Wave 2:**
  - DEC-3: `GameSettings` coupling out of Players; owns `PlayScene.cs`.
  - DEC-6: `EnemyVisualState` moves into `EnemyRenderer`.
  - DEC-7: runtime close-out; owns `WorldSessionRuntime.cs`.
  - MEM-1: animated model path; owns `AnimatedModel.cs`, `Shader.cs` and the player renderers.
  - MEM-5: world batching and presentation capture.
  - MEM-6: HUD text and `Render.cs`.
  - WIRE-4: pooled single-pass encode.
- **Wave 3:**
  - NET-1 with NET-6: seeded combat rolls and canonical tick order; owns `WorldSessionRuntime.cs`, `DedicatedServerHost.cs` and `RuntimeAuthoritativeSessionHost.cs`.
  - GFX-3: `Render` owns and disposes everything.
  - GFX-7: glTF and texture decode split from upload.
  - MEM-7: client orchestration and appliers.
  - WIRE-5: encode each snapshot once for all connections, and fix the sequence race.
  - WIRE-6: protocol v2 slimming.
- **Wave 4:**
  - MEM-3: runtime tick allocations.
  - GFX-4: scene teardown.
  - NET-7: simulation checksum and a determinism test across two runtimes.
- **Wave 5:**
  - DEC-8: `loadVisualAssets` removal and a whole-Simulation dependency assertion.
  - GFX-8: async model cache.
- **Wave 6:** NET-4: enemy interpolation.
- **Added and done after wave 1:** a configurable frame cap (`max_fps`, default 144), chosen over VSync by the user.
- **Deferred, design-gated:**
  - NET-5: buffered command stream and protocol bump.
  - WIRE-7: delta actors.
  - WIRE-8: JSON source generation.
  - The cube part of NET-4 (cube render interpolation).
  - NET-2's map seed in the mutation delta.
  - Map-internal allocations in `UpdateFracturePiecePositions` and `UpdateCubePosition`.
  - Moving `Dream.Server.Rules` out of Simulation.

The full analyst write-ups (evidence, risks, tests) are in the iteration work-log.

## Integration

The integrator creates `integrate/engine-iteration` from `iteration/engine-base` and merges the wave's task branches in table order, building after each merge. The tester and reviewer then run on the integration branch. Fix loops are capped at two, then the user is asked. Nothing is pushed or merged into `master`.

## Revised Waves 5 And 6 (after wave 4)

- **Wave 5:**
  - **DEC-8:** remove `loadVisualAssets`.
  - **GFX-8:** async model cache, so nothing loads synchronously during a draw.
  - **MEM-10:** remove `WorldPhysicsBinder` move allocations.
  - **MEM-11:** remove `Player` collision-check and grounded-probe allocations.
  - **WIRE-10:** hand-written converters for the command and actor-snapshot types, plus `[JsonIgnore]` on the derived properties. This replaces WIRE-8, which was not merged; see the wave-4 work-log.
- **Wave 6:**
  - **NET-4:** interpolate remote enemies on the tick timeline.
  - **NET-8:** a per-server simulation seed, persisted.
  - **GFX-9:** each enemy gets its own model instance, so clips differ per wolf.
  - **NET-9:** add a public lookup from a cube to its replicated id, and key the checksum by that id.

## Wave 6 (revised after wave 5)

- **NET-4:** interpolate remote enemies on the tick timeline. It also owns `EnemyRenderer` this wave.
- **NET-8:** per-server simulation seed, persisted with the world.
- **NET-9:** a public lookup from cube to replicated id, with the checksum keyed by that id.
- **WIRE-11:** a hand-written converter for `ServerSnapshotMessage`, to remove the remaining 824 B per snapshot encode.
- **TEST-1:** isolate the timing-sensitive tests (`TickPacerTests`, `MultiClientLoopbackSmokeTests`) from parallel load.
- **Deferred to wave 7:** GFX-9, a model instance per enemy. It shares `EnemyRenderer` with NET-4.

