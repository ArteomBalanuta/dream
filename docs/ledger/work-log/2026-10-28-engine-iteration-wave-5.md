# Engine Iteration Wave 5: Async Models, Converters, Binder and Player Allocations, Decoupling Cleanup

Purpose: Record wave 5 of `docs/ledger/plans/2026-10-27-engine-iteration-master-plan.md` (revised waves) and its fix loop.

Status: integrated on `integrate/engine-iteration` and verified by automated tests and a perf-harness run. Not pushed or merged into `master`. Run in continuous mode.

## Process

1. **Implementers.** Five implementers ran in parallel from `700c67f`: DEC-8, GFX-8, MEM-10, MEM-11 and WIRE-10.
2. **Integration.** All five merges were clean. DEC-8 went last because it removes a parameter used in about 60 test files.
3. **Review and test.**
   - The reviewer found no Must fix items and 4 Should fix items.
   - The tester added 9 cross-task tests. It found one production bug that predates this wave: creep below 1 mm per tick was never replicated.
   - A perf run was done on the merged build.
4. **Fix loop 1.** Three implementers:
   - A: player disposal, the camera scratch and a diagnostics guard.
   - B: replication of sub-millimetre creep.
   - C: remote model streaming and a stricter ownership test.

## Changes

| Task | Change |
| --- | --- |
| DEC-8 | Removed `loadVisualAssets` from `Map`, `WorldGenerator`, `WorldSessionRuntime` and `ClientWorldSession`, and from 61 test files. |
| GFX-8 | `AsyncResourceLoader` decodes on the thread pool, uploads only through a render-thread pump (two per frame), and has per-instance handles. `ModelAssets` gains `TryGet`, `Preload`, `PumpUploads` and `RequestPlayerInstance`; the blocking getters are removed. The player, enemy and viewmodel renderers skip a model until it is ready. Cubes use the untextured fallback until then. `ResolveCubeAssetScale` compares `SourcePath`. `PlayScene` preloads and pumps. |
| MEM-10 | `WorldPhysicsBinder` uses loops and reuses its per-cell lists. It writes a body's boxed `(Cube, Vector3)` `UserObject` in place; every body owns its own box. `WorldSpatialIndex` reuses its sector lists. |
| MEM-11 | `CheckAndLogCollisions` only logs, so it now runs only when diagnostics are on. `Player` owns one contact callback and shares it with `IsGrounded`. `Player` is now `IDisposable`. Added `Camera.CopyTo` and `Player.CopyPresentationCameraTo`. |
| WIRE-10 | Hand-written `JsonConverter`s for `PlayerCommandMessage`, `PlayerCommandFrame` and its intents, `ActorSnapshot`, its stats and progression, and the actor list. The wire is byte-identical to the v2 reflection output, and reads tolerate any property order, unknown properties and missing optional properties. |
| Fix A | `RemoteSessionPlayerState`, the `WorldSessionRuntime` teardown (primary player plus every remaining secondary) and `ClientWorldSession` now dispose their `Player`s, each exactly once. `ClientWorldSession` reuses a scratch first-person camera, and `ThirdPersonCameraRig.ResolveInto` was added. The tests with a 0 B tick budget now fail with a clear message when diagnostics are on. |
| Fix B | `PhysicsSyncSystem` reports a cube move once the cube has moved more than 1 mm from the center it last reported (or changed cell), instead of comparing against the previous tick. The per-cube baseline is pruned when cubes leave. |
| Fix C | Remote player models are requested only within 1.25× render distance and released beyond 1.5×, with at most 4 pending requests. The local player's model loads lazily on its first third-person draw. The ownership test strips comments and requires the call shape. |

## Measured Impact

Bytes per call after warm-up, measured with `AllocationAssert`. Each "before" was measured on the original code.

| Path | Before | After |
| --- | --- | --- |
| `PlayerCommandMessage` encode (WIRE-10) | 520 | 0 |
| 24-actor snapshot encode (WIRE-10) | 5,040 | 824 |
| 24-actor snapshot decode (WIRE-10) | 14,736 | 7,016 |
| Client applying two cube moves (MEM-10) | 400 | 0 |
| Binder: same-cell move / cell change with sync / reindex (MEM-10) | 200 / 552 / 376 | 0 / 0 / 0 |
| Sector crossing (MEM-10) | 88 | 0 |
| Server runtime tick, 3 sessions (MEM-11) | 30 | 0 |
| Tick plus snapshot export (MEM-11) | 616 | 584 (exactly the wire payload) |
| Collision period on a cube (MEM-11) | 1,368 | 0 with diagnostics off |
| `IsGrounded` (MEM-11) | 504 | 80 (allocated inside BulletSharp's contact callback) |
| Presentation capture (fix A) | 776 | 696 |

## Perf Harness

Run: `logs/perf-run-20261028-012403` in the integration worktree, taken before the fix loop.

- Both clients held 144 FPS. Frame medians were 0.25 and 0.51 ms.
- The remote player's model loaded asynchronously (`cachedModels=1` in 33 of 35 samples).
- The worst remote-player draw was 14 ms, against 63 ms in the wave-2 run, which still loaded synchronously inside draw.
- The server tick median was 0.06 ms, with one tick per loop and no errors.

## Verification

- `dotnet build dream/dream.csproj` finished with 0 errors.
- `dotnet test dream.Tests/dream.Tests.csproj`: 1,497 of 1,497 passed in two runs, with 0 skipped.
- The tester's cross-task checks:
  - **Bodies (540 ticks).** Every body's `UserObject` equals map state, no two bodies share a box, and invariants hold on the server and its client mirror. The run had 3,164 moves, 154 cell changes, 14 sector changes and 3 fractures.
  - **Converters.** Byte-identity on 16 threads and over real TCP with 3 clients for 200 ticks.
  - **Loader.** Stress-tested with 3 seeds: at most one upload, exactly one dispose, no retry after a failure, no deadlock.
  - **Contact callbacks.** Each shared callback gives the same results as a fresh one.
- **Intermittent timing failures.** `TickPacerTests` and `MultiClientLoopbackSmokeTests` each failed once in several implementer runs under heavy parallel load, and passed on their own. They are scheduled for isolation in wave 6.
- **Not verified:**
  - Interactive play.
  - How model pop-in looks on screen.
  - The renderer distance policy was tested only with fake handles.

## Known Limits And Follow-ups

- Snapshot encode still allocates 824 B inside the reflection serializer for `ServerSnapshotEnvelope`; a converter would remove most of it (WIRE-11).
- `IsGrounded` keeps 80 B per jump inside BulletSharp.
- `ClientWorldSession` still allocates the presentation camera per capture, because a test keeps captures across frames.
- `RemotePlayerRenderStats` does not split ready and pending models.
- Still open: enemy interpolation (NET-4), a per-server seed (NET-8), checksum keying by replicated id (NET-9), and a separate model instance for each wolf (GFX-9).
