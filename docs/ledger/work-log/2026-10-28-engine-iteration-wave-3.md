# Engine Iteration Wave 3: Determinism, GL Ownership, Decode Split, Client Orchestration, Encode Once, Protocol v2

Purpose: Record wave 3 of `docs/ledger/plans/2026-10-27-engine-iteration-master-plan.md`: six tasks, their integration and verification, and two fix loops.

Status: Integrated on `integrate/engine-iteration` (`fc9ef46` plus this record). Verified by automated tests and two perf-harness runs. Not pushed and not merged into `master`.

## Process

1. **Implementers.** Six implementers ran in parallel from `integrate/engine-iteration` at `619878e`. Each worked in its own worktree and owned separate files.
2. **Integration.** All six merges were clean. Then 1,253 of 1,253 tests passed, twice.
3. **Verification.**
   - Reviewer: no Must fix, 2 Should fix and 8 nits.
   - Tester: added 11 cross-task tests and found two production issues: spawn order depended on join order, and a version mismatch disconnected silently.
   - Perf run on the merged build.
4. **Fix loop 1.** Three implementers, one each for:
   - protocol mismatch handling and the smoke test;
   - tick fairness and deferred joins;
   - render disposal nits.
5. **Fix loop 2.** This was the last one allowed. One implementer wired deferred joins into the live server. It also found and fixed a bug where a refused join skipped the next join in the same tick.

## Changes

| Task | Change |
| --- | --- |
| NET-1/6 | Combat rolls use `DeterministicRandom` seeded from simulation seed, tick, session hash, action sequence and target actor id. `WorldSessionRuntime.SimulationSeed` defaults to `DefaultSimulationSeed`, so `Random.Shared` is gone from `Simulation`. Joined sessions, secondary commands and secondary players are processed in ordinal session-id order. The sorts are in place and allocation-free per tick. |
| GFX-3 | `Render : IDisposable` releases child renderers, shaders (now in `DisposableRegistry`), debug buffers, HUD resources and the ImGui context, one step at a time, and collects failures. `LoadShader` disposes the shader it replaces. `WorldRenderer`, `LocalPlayerRenderer` and `RemotePlayerRenderer` are `IDisposable`. User-controlled HUD text goes through `TextUnformatted` or `HudText`, so `%` is no longer read as a format character. |
| GFX-7 | `AnimatedModelData` (no GL) holds the parsed glTF, processed vertices, decoded textures, skin joints, animations and pose. `AnimatedModel(AnimatedModelData)` only uploads. `TextureDecoder` decodes and uploads separately. `StaticTerrainTextureCache` decodes on the thread pool and uploads on the render thread. A fault is reported once and cached, and `Dispose` observes pending work. |
| MEM-7 | `PlayScene` uses a static tick lambda, `TryDrain` and `GetTimestamp`. The time before the world draw is logged as `preDraw` (clear, capture, remote states). `NetworkDiagnosticsLog` gains state overloads and a thread-local capture hook for tests. `ClientScenePresenter` caches the roster and reuses its lists. The appliers no longer allocate closures or sets per snapshot. |
| WIRE-5 | `SharedEncodedPayload` encodes each tick's snapshot once, and every connection writes it with `WriteEnvelope` into its own `PooledFrameWriter`. Queue nodes are reused. Sequence numbers are assigned and frames encoded under `_sendLock`, which fixes the server-side sequence race. A merged snapshot for a lagging client is encoded per connection. |
| WIRE-6 | **Protocol v2.** <ul><li>`WhenWritingNull`.</li><li>A resolver modifier drops the leaked `messageKind` and the duplicated `effectiveRoster` and `effectiveInitialActors`.</li><li>`CurrentProtocolVersion = 2`.</li></ul> A v1 peer is rejected by the version check. |
| Fix A | A version mismatch now gets a v2 rejection frame with a readable reason, and the server logs it, with no unobserved task exception. The server connection loop catches and logs every error, and its `finally` cannot fault. On the client, `ConnectAsync` waits up to 2 s for `ServerHello`. If the hello reports another version, connect fails with `ProtocolVersionMismatchException`, for example "Server uses protocol v1; this client uses v2." The smoke test's walk window is anchored to session-b first seeing itself. |
| Fix B | The primary seat's use action resolves in the same ordinal pass as the secondaries, after physics, so the first-joined seat no longer wins same-tick ties. This also covers two players building in the same cell, and the primary now receives its own action result. `DedicatedServerHost` can defer joins to the tick and apply them in ordinal order. |
| Fix C | `Render.Dispose` asserts the render thread before marking itself disposed, and each HUD resource has its own dispose step. `RemotePlayerRenderer` disposes its models before the tracker cleanup. A primitive with no material uses the glTF default material instead of the previous draw's uniforms. |
| Fix loop 2 | Both the dedicated and the integrated server build the host with `deferJoinsToTick: true`. `DedicatedServerService` delivers join replies after the tick, before that tick's snapshot. `ApplyPendingJoins` swaps lists, so a refused join no longer makes the loop skip the next join. |

## Measured Impact

Measurements are in bytes per call after warm-up (`AllocationAssert`), with "before" measured on the original code, unless marked otherwise.

| Path | Before | After |
| --- | --- | --- |
| Server, whole process, per tick, 2 / 8 clients (WIRE-5) | 15,664 / 96,904 B | 2,712 / 7,920 B |
| Snapshot encodes per tick (WIRE-5) | N | 1 |
| Snapshot payload, 4 players, 20 enemies (WIRE-6) | 8,384 B | 6,363 B (−24%) |
| Join payload, 4 players, 24 actors (WIRE-6) | 16,378 B | 6,808 B (−58%) |
| Live snapshot, 2 players, in the perf run (WIRE-6) | 1,525 B | 1,277 B (−16%) |
| `BuildPlayerRoster` (MEM-7) | 1,032 B | 0 |
| `ApplySnapshot`, 6 actors (MEM-7) | 1,152 B | 0 |
| Tick drain + tick lambda + 7 stopwatches (MEM-7) | 776 B | 0 |
| `RemoteEnemyStateApplier`, 3 wolves (MEM-7) | 1,064 B | 352 B (the rest is in `EnemyRegistry`) |

## Determinism

The tester ran two `DedicatedServerHost` + runtime instances in one process for 300 ticks. The run covered walking, two players killing a 400 HP enemy, the wolf fighting back, and building a cube until it fractured into 6 rubble pieces.

| Scenario | Result |
| --- | --- |
| Same seed, different connection ids and command delivery order | Bit-identical every tick |
| Different seed | Diverges exactly at the first damage roll (tick 30) |
| Same-tick joins in different arrival orders (after fix B and fix loop 2) | Identical seats, actor ids, spawn points and per-tick state |

The live server path is covered by `LiveServerDeferredJoinLoopbackTests`.

Joins in different ticks are a different input history, so they fall outside this claim. All of this is same process and same platform.

## Perf Harness

Both runs used one dedicated server and two clients at the 144 FPS frame cap.

**Wave-3 merge** (`logs/perf-run-20261027-232218`):
- Both clients held 144 FPS, and protocol 2 was negotiated.
- `preDraw` median was 0.01 ms, which shows the wave-2 rise in total frame time was not CPU work in that span.
- Focused client median frame was 0.57 ms, and server `advanceMs` median was 0.06 ms.

**After fix loop 2** (`logs/perf-run-20261028-001841`):
- Both clients held 144 FPS, with frame medians of 0.25 and 0.50 ms.
- Both joined through the deferred-join path and rendered each other.
- Server `advanceMs` median was 0.06 ms, and all 36 sampled loops ran exactly one tick.
- No exceptions, render faults or resyncs.
- The only disconnect lines were the clients logging their new `DisconnectReason` when the harness stopped the server.

## Verification

- `dotnet build dream/dream.csproj`: 0 errors.
- `dotnet test dream.Tests/dream.Tests.csproj`: 1,297 of 1,297 passed, twice after fix loop 2. Implementers ran it about 40 more times on their branches.
- Intermittent failures during implementer runs:
  - `MultiClientLoopbackSmokeTests`: twice; one root cause was fixed.
  - `TickPacerTests`: once.
  - `PooledClientSendLoopbackIntegrationTests`: once.

  None failed in the integration runs. All three tests are timing-sensitive.
- Not verified:
  - Interactive play.
  - That HUD text and terrain textures appearing a frame late look right on screen.
  - `Render.Dispose` in the running game: nothing calls it until GFX-4.

## Behaviour Changes To Watch

- **Protocol v2.** Clients and servers from before this wave cannot connect. They now get a clear version error instead of a silent disconnect.
- **Joins.** A join is answered at the next server tick, about 16 ms later at most.
- **Primary-seat action.** It now resolves after physics, the same as the other seats, and returns an action result to the primary client.
- **Terrain textures.** They draw untextured for at least one frame per material and role until the background decode finishes.
- **Snapshot and roster actor order.** It is ordinal by session id.

## Known Limits And Follow-ups

- All hosts use the public `DefaultSimulationSeed`. Rolls can be computed from public inputs; a per-server seed, persisted if replays are wanted, is a follow-up.
- `SharedEncodedPayload` uses `Lazy` with `ExecutionAndPublication`, so N−1 writer threads block while one encodes.
- `EnemyRegistry` still allocates closures (MEM-3), and the `Map` move and fracture updates still allocate.
- `PlayScene` does not yet dispose `Render` or the model assets (GFX-4).
- Model loading is still synchronous for callers (GFX-8).
- `FramePerformanceSnapshot` has no `preDraw` field, so that time is only in the log line.
