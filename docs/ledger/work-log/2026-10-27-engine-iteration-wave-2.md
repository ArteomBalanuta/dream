# Engine Iteration Wave 2: Decoupling Close-out, Animation, Batching, HUD, Pooled Encode

Purpose: Record wave 2 of `docs/ledger/plans/2026-10-27-engine-iteration-master-plan.md`, which covers seven tasks, their integration, verification and one fix loop.

Status: Integrated on `integrate/engine-iteration` and verified by automated tests and a perf-harness run. Not pushed and not merged into `master`.

## Process

1. **Implementers.** Seven implementers ran in parallel, each in its own worktree and each owning separate files. All started from `integrate/engine-iteration` at `5b9ee95`: wave 1, the fix loops and the frame cap.
2. **Integration.**
   - All seven merges were clean.
   - One integration commit asserts that the whole `dream/Simulation` tree is free of client, rendering, application, ImGui and windowing references.
3. **Verification.** The tester and reviewer ran in parallel, and a perf run was done on the merged build.
   - **Reviewer:** no Must fix items; 2 Should fix items and 6 nits.
   - **Tester:** 16 cross-task tests and no production bugs. The tester found that about half of full-suite runs had 1–2 failures, all in older tests or the test helpers.
4. **Fix loop 1.** Two implementers ran in parallel: production findings, and test reliability.

## Changes

| Task | Change |
| --- | --- |
| DEC-3 | `PlayerSimulationTuning.FromSettings(GameSettings)` became `GameSettings.ToPlayerSimulationTuning()` in `Dream.Application`. `Simulation/Players` no longer references `Dream.Application`. |
| DEC-6 | Enemy clip state moved from `EnemyActor.VisualState` to `EnemyVisualStateCache`, owned by `EnemyRenderer`. The cache is keyed by actor id and pruned each frame. |
| DEC-7 | The authoritative runtime changed in three ways: <ul><li>It no longer implements `IClientWorldState`, which leaves `ClientWorldSession` as the only implementation.</li><li>The wolf gameplay constants moved to `Simulation.Enemies.WolfDefinition`, and `WolfAssetDefinition` re-exposes them.</li><li>Movement goes through `PlayerMovementResolver`.</li></ul>Three client tests that relied on runtime-only dynamic bodies were split. Each keeps its original assertions on a map built the way the runtime builds one, and gains a client version that checks kinematic bodies. |
| MEM-1 | `AnimatedModelPose` holds each model's pose and samples keyframes itself: LINEAR, STEP, slerp along the shortest path, with CUBICSPLINE falling back to SharpGLTF. Other changes: <ul><li>A flattened draw list of renderable nodes.</li><li>Cached uniform locations for joint matrices, through `Shader.GetUniformLocations`.</li><li>A shared head-override dictionary.</li><li>Reusable bookkeeping for remote players.</li></ul>The glTF nodes are no longer changed at runtime. |
| MEM-5 | `WorldRenderBatchPlanner` became an instance that pools its lists and keeps batches in first-seen order. `WorldRenderer` reuses its visible lists. `ActiveEnemies` is returned without copying while every enemy is active. The `Stopwatch` timers became `GetTimestamp` calls. |
| MEM-6 | `HudText` formats into UTF-8 with an allocation-free interpolated-string handler and draws with `igTextUnformatted`. Also: <ul><li>Status labels and health labels are cached.</li><li>`CollisionDebugBodySelector` sorts in place, keeping the order of equal distances.</li><li>The cube face ranges are a static array.</li><li>The inventory slot lookup is a loop.</li></ul> |
| WIRE-4 | Encoding writes the payload in one pass into an `IBufferWriter`, with `EncodePayload` and `WriteEnvelope` for WIRE-5. Also: <ul><li>`IMessageFramer.WriteHeader`.</li><li>`PooledFrameWriter`, backed by `ArrayPool`, which grows and shrinks after a frame over 64 KiB.</li><li>The client send path encodes and assigns sequence numbers inside `_sendLock`, which fixes a client-side sequence race.</li></ul>The wire bytes are identical to the old encoder. |
| Integration | One test covers the whole `dream/Simulation` tree. |
| Fix A | `RemoteClientSession.DisposeAsync` takes the send lock before returning the pooled buffer. Sends started after disposal throw `ObjectDisposedException`, and dispose can safely be called twice. Also: <ul><li>New tests for the STEP, single-key and CUBICSPLINE sampler paths, which match SharpGLTF exactly.</li><li>The long-line fallbacks use `TextUnformatted`.</li><li>`GetUniformLocations` returns a `ReadOnlySpan<int>`.</li></ul> |
| Fix B | Test reliability: <ul><li>`AllocationAssert` warms up at least 64 calls, then measures for at least 5 rounds and 300 ms, keeping the minimum. This keeps JIT tiering inside the measured window from adding bytes.</li><li>`ShaderLifetimeTests` uses a dedicated thread.</li><li>`MultiClientLoopbackSmokeTests` is driven by server tick and the player's observed state instead of a wall-clock schedule.</li><li>The dependency tokens match whole names, so a bare `using Dream.Rendering;` is caught.</li></ul> |

## Measured Impact

All figures are bytes allocated per call after warm-up, measured with `AllocationAssert`. Each "before" was measured on the original code.

| Path | Before | After |
| --- | --- | --- |
| Wolf joint world matrices, per primitive per draw (MEM-1) | 238,792 | 0 |
| Wolf animation update, Walk clip (MEM-1) | 143,928 (range 111,944–241,096 across clips) | 0 |
| Player animation update, idle and walk (MEM-1) | 22,152 / 22,608 | 0 |
| Wolf renderable traversal (MEM-1) | 14,904 | 0 |
| `PlayerRenderState` create (MEM-1) | 1,064 | 0 |
| Batching 1,000 blocks (MEM-5) | 221,848 (on the large-object heap every frame) | 0 |
| Batching 1,000 cubes (MEM-5) | 26,712 | 0 |
| HUD performance lines, 13 lines (MEM-6) | 1,776 | 0 |
| World labels, 4 players and 8 enemies (MEM-6) | 1,752 | 0 |
| Debug collision selection, 200 bodies (MEM-6) | 14,056 | 0 |
| Client command encode and frame (WIRE-4) | 2,608 | 520 (all inside the System.Text.Json payload serializer; envelope and framing are 0) |

**MEM-1 accuracy.** Compared with SharpGLTF over every clip and 4,335 sampled times, the largest error is 5.3e-5 absolute on the wolf's joint matrices, which are about 100× scale; that is 5.4e-7 relative. The synthetic STEP, single-key and CUBICSPLINE models match exactly.

## Perf Harness

Setup: one dedicated server and two clients for 30 s, frame cap 144. Logs: `logs/perf-run-20261027-220611` in the integration worktree. The run came before the fix loop, which changed only the client dispose, the text fallbacks, the uniform span and tests.

Both clients held 144 FPS and rendered each other. The server tick median was 0.06 ms, with one tick per loop. No exceptions, render faults or resyncs were logged.

**Wolf draw ("enemy").** The median went from 0.38–0.41 ms to 0.04–0.06 ms.

**Unexplained focused-client frame time.**
- The focused client's total frame median rose from 0.54 ms (the previous capped run) to 1.14 ms, while every timed part of the frame went down.
- The extra time is in the part of `PlayScene.Render` that has no timer: `GL.Clear`, presentation capture and `BuildRemotePlayerStates`.
- The unfocused client moved the other way, from 0.60 ms to 0.26 ms. That suggests a GPU sync stall moving from the enemy draw into `GL.Clear`, not new CPU work.
- This is not confirmed. Both figures are far below the 6.9 ms budget at 144 FPS. A follow-up should add timers to that part of the frame.

**First-send latency.** Client `sendMs` now includes encoding. The first hello and the first command took about 16 ms, because the encoder is used for the first time. The median is 0.03 ms.

## Verification

- `dotnet build dream/dream.csproj` succeeded with no errors.
- `dotnet test dream.Tests/dream.Tests.csproj`:
  - After fix loop 1: 1,076 of 1,076 passed, three times in a row.
  - Before fix loop 1: 1,049 of 1,049 passed (base 942).
- **Flakiness**, over 16 full runs with default runtime settings each:
  - Before fix B: 9 runs had a failure (7 in the smoke test, 3 in allocation tests).
  - After fix B: 0.
- **Acceptance:**
  - `dream/Simulation` has no `Dream.Client`, `Dream.Rendering`, `Dream.Application`, ImGui or OpenTK windowing or graphics references, checked by whole-name matching.
  - `dream/Simulation` has no `Dream.Configuration` or `GameSettings` references.
  - `WorldSessionRuntime` does not implement `IClientWorldState`.
- **Not verified:**
  - Interactive play by a person.
  - Whether wolf and player animation looks right in the running game; only numerical parity with SharpGLTF was checked.

## Known Limits And Follow-ups

- Several `ImGui.Text($"...")` calls in `Render.cs` (connected server, player and session, and others) still pass text as a printf format, so a `%` is misread.
- All wolves share one `AnimatedModel`. Wolves in different states therefore show the clip applied last, and `Update` runs once per wolf. This behaviour predates wave 2; the clip state is per actor but the pose is per model.
- The 520 B left per command encode needs source generation or hand-written converters (WIRE-8).
- `EncodePayload` and `WriteEnvelope` are not used by production code until WIRE-5.
- `AllocationAssert` now reports the smallest round. A periodic, amortised allocation could be hidden by it.
- The `loadVisualAssets` plumbing is still dead (DEC-8). `Dream.Server.*` is still referenced from `Simulation`.
