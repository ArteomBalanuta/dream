# Engine Iteration Wave 7: Metrics, Fast Tests, GL Hygiene, Off-Thread Chunk Planning, Seat Cadence, Input Validation

Purpose: Record wave 7 of `docs/ledger/plans/2026-10-28-engine-iteration-round-2-plan.md`, its two fix loops and the Bullet teardown crash fix.

Status: Integrated on `integrate/engine-iteration` and verified by automated tests and a perf-harness run. Not pushed. Not merged into `master`. Run in continuous mode.

## Process

1. Eight implementers ran in parallel from `57cdf83`: SCALE-6, ARCH-1, R-HYG (ARCH-5 + GPU-8), ARCH-6, GPU-1, GPU-5, NETR-3 and NETR-6.
2. Integration (`5d7f959`):
   - Seven merges were clean.
   - ARCH-1 conflicted with NETR-6. ARCH-1 had split the loopback test classes that NETR-6 retargeted, so NETR-6's changes were re-applied inside the split classes.
3. Verification:
   - The reviewer found 1 Must fix and 6 Should fix items.
   - The tester added 25 tests and found 2 production bugs, 1 latent state gap and 1 user-experience trade-off.
   - A perf run was taken.
4. Fix loop 1 ran six implementers in parallel: render state, chunk uploads, runtime use and stats, server host and metrics, client world apply, and test GC mode. The re-review found no Must or Should fix items.
5. An extra fix addressed a native Bullet crash in teardown. It caused intermittent test-host crashes and hung test hosts.
6. Fix loop 2 fixed the intermittent restart-determinism failure, a production persistence dispose bug.

## Changes

| Task | Change |
| --- | --- |
| SCALE-6 | `ServerTickMetrics` is always on, with no allocation: a 5% fixed-bucket histogram (p50/p95/p99/max), dropped ticks, checkpoint timings and write-queue depth. `ServerConnection` counts bytes and frames. A headless load harness lives in `dream.Tests/Load` (BotClient over the real `RemoteClientSession`, `WorldSeeder`, `LoadRunner` with JSON and Markdown reports); its tests run only when `DREAM_LOAD_TEST=1`. |
| ARCH-1 | The full suite now runs in about 25 s instead of 90 s, with the serial phase down from 53 s to 6 s. Changes: a thread-pool minimum set in a module initializer, `xunit.runner.json`, waits based on progress instead of wall-clock floors, fixtures for the startup map and the glTF models, long classes split by scenario, and server loops accepting on the thread pool. |
| R-HYG | Compiler warnings went from 29 to 13. Obsolete GL enums were replaced, nullable warnings fixed, `SQLitePCLRaw` pinned to 2.1.12 to clear an advisory, and `DrawSolidCube` deleted. The ImGui pass now enables scissor, selects texture unit 0, caches uniform locations, sets up its vertex array once, uploads the font as RGBA, and saves and restores GL state. |
| ARCH-6 | `PlayerController` is now static. Removed `Map.CaptureGeometryChunkSnapshot`, `GetNearbySolidCubes` and `GeometryChunkSnapshot`. Added `InternalsVisibleTo("dream.Tests")`. |
| GPU-1 | Static chunk planning runs on the build worker, using a neighbour lookup instead of copying 27 chunks. Only metadata is kept after upload, VAOs and VBOs are reused, and uploads are limited to 2 MiB per frame. |
| GPU-5 | `GpuFrameTimer` (a ring of 4 time-elapsed queries, read without stalling), `RenderCounters`, a `GPU:` HUD line, a KHR_debug callback in DEBUG builds, and a clean window close on fatal GL errors. |
| NETR-3 | Every seat now uses the same per-tick primary-use cadence. Bug: a secondary seat holding build placed up to 64 cubes/s; it now places 3. After 16 ticks without a command, a seat decays to an idle command. The MEM-3 golden checkpoints 6–10 were re-pinned, with proof that the old gate reproduces the old values. |
| NETR-6 | Clients can no longer send `BuildUseIntent` or `CombatUseIntent`. Command frames are sanitized: axes clamped to [-1, 1], non-finite values removed. Each session's action queue holds at most 16 actions and is purged on release. World resync is deferred to after the tick with a per-connection cooldown. The noclip/freeze gate was not added; it is a user decision. |
| Fix loop 1, render | The world pass now sets blend off and depth test on itself, `fragmentShader` writes alpha 1, and the global blend default was removed. Must fix: the collision debug wireframes had been invisible. The ImGui snapshot now also saves the scissor box and array buffer. `CountDraw` is called at all 7 draw sites. A fatal GL error stays latched until frame end. |
| Fix loop 1, chunks | A superseded build batch is now uploaded instead of dropped. Bug: continuous edits used to starve uploads forever. The signature snapshot is taken in `StartBuild` and includes the pending batch's outputs. Bug: reverting an edit could skip rebuilding a neighbour chunk. |
| Fix loop 1, runtime | Server-made uses go through `TryProcessCommandPrimaryUse`. Bug: a held use was rejected as stale. Stats updates outside the editor ranges, or NaN, are now rejected. |
| Fix loop 1, server | `DroppedTicks` is read from the host, and the mirror clock was removed. `ReleaseConnection` now runs entirely under the authority lock. Bug: a reconnect reusing the session id could lose its actions or its seat. Other changes: resync cooldown 1.5 s, `IPlayerActionQueue.TryEnqueue`, metrics recorded under one lock, the write-queue depth counted with `Interlocked`, and closed-connection totals kept under the same lock as connection removal. |
| Fix loop 1, client | The client no longer re-applies an accepted build result to its world; the sequenced snapshot mutation is the only authority. Bug: a cube built on the floor became Grass with no id, the checksum mismatched, and a full resync followed on every such build. |
| Fix loop 1, tests | The test project uses workstation concurrent GC, the same as production. The load report header states the GC mode. |
| Fix loop 2 | `PersistenceWriteQueue` and `BackgroundCheckpointWriter` now finish every queued write on dispose. Before, they returned after 10 s or 5 s with writes still queued, and the worker kept writing after the server was disposed. Bug: those late profile writes landed after a database wipe or restart, so the restored players had the old health, experience and mode. Shutdown now blocks until the queue is empty. SCALE-3's group commit will shorten that wait. |
| Bullet teardown | `PhysicsIntegrator` suppresses BulletSharp's finalizers and disposes in native order. An undisposed world now leaks instead of crashing. Leaks fixed in `IntegratedLocalServerHandle` (failed start) and in 3 tests. A leak tracker is available behind `DREAM_PHYSICS_LEAK_TRACKING=1`. |

## Measured Impact

| Measurement | Before | After |
| --- | --- | --- |
| Full test suite (wall) | 90–96 s | 35–38 s with workstation GC (25 s with server GC before fix loop 1) |
| Render thread on a static rebuild, startup world (GPU-1) | 16.05 ms | 0.93–1.4 ms |
| Worker plan, startup world, Release (GPU-1) | 9.2 ms | 4.0 ms |
| CPU mesh lists kept after upload (GPU-1) | about 6.5 MB | 0 |
| Secondary seat holding build for 1 s (NETR-3) | 64 cubes | 3 cubes |
| Compiler warnings, `dream.csproj` (R-HYG) | 29 | 13 (all from ImageSharp advisories, plus 1 in `DedicatedServerHost`) |
| Presentation capture (GPU-5 struct growth) | 696 B | 744 B, still a single allocation |

Load baselines from SCALE-6. Release build, 16 logical CPUs; the machine was shared with other agents.

| Scenario | Loop p50 / p99 | Per-tick p50 | Ticks/s | Server to client per client | Join |
| --- | --- | --- | --- | --- | --- |
| Smoke8 (8 bots, 2k cubes) | 19.2 / 21.2 ms | 9.7 ms | 64, 2 ticks per loop | 147 KB/s | 268 ms |
| Players32 | 34.6 / 40.0 ms | 11.8 ms | 64, 3 ticks per loop | 527 KB/s | 1093 ms |
| World10k (10k cubes, 1k rubble) | 646 / 678 ms | 83 ms | 12.5, 1310 dropped | 11.6 KB/s | needs a 32 MB frame |

## Perf Harness

Run `logs/perf-run-20261028-045303` on `5d7f959`, before the fix loops:

- Both clients held the 144 FPS cap, with CPU frame time of 0.4–0.8 ms.
- The new `GPU:` line read world 0.15 ms, actors 0.04 ms, about 89k primitives, 81 programs and 1,198 uniforms per frame.
- Two-player snapshot: 1,329 B.
- No errors.

## Verification

- `dotnet build dream/dream.csproj`: 0 errors, 13 warnings.
- `dotnet test dream.Tests/dream.Tests.csproj`: 1,821 passed and 4 skipped (the load tests, which are gated), with no test-host crashes after the Bullet fix.
- Before fix loop 2, `PersistentWorldSeedDeterminism…Restart…` failed in 2 of about 21 full runs, diverging at tick 1 in the actors part. After the fix, 9 full runs in a row passed, and the class passed 10 times alongside 4 concurrent suites.
- One flake remains: `LiveServerDeferredJoinLoopbackTests.DeferredJoin_FromAConnectionReleasedBeforeTheTick_GetsNoSeat` failed once (expected primary `session-b`, got null). It touches the release path that fix loop 1 put under the lock, and it goes to wave 8 (SCALE-3).
- The tester checked, among other things:
  - plan equivalence over 50 random cross-border edits for each of 3 seeds;
  - late-join fracture convergence under the deferred resync;
  - sanitized-input cadence on both seats;
  - service byte totals across a disconnect.
- Not verified: interactive play, blend changes on real GPUs, and the KHR_debug callback on hardware.

## Known Limits And Follow-ups

**Stall snap (UX trade-off).** A client stall of more than 16 ticks now snaps back: about 4.5 m after a 1 s stall, because the server idles the seat and `LatestPlayerCommandInbox` keeps only the newest frame. NETR-4 and NETR-5 (wave 8) fix this.

**Scale.** 2k dynamic cubes cost about 10 ms per tick, so the loop runs 2 ticks per iteration and snapshots go out at 32 Hz (SCALE-5). Joins stall the tick for 250–550 ms (SCALE-3). World10k cannot keep real time or join at a 1 MB frame (SCALE-5, SCALE-1).

**Decisions for the user:**
- ImageSharp advisories: version 3.2.0 clears them but needs a Six Labors license key for Release builds.
- `maxParallelThreads: "unlimited"` versus a faster `"2x"`, which would need four test classes fixed.
- The noclip/freeze gate (NETR-6).

**Reviewer nits carried into wave 8:**
- Stale comments in `WorldSessionRuntime`, and `TryCreateCommandPrimaryUseActionMessage` is still public.
- `DrawCubeTransparent` is unused.
- Opaque-only shaders ignore `alphaMode` (GPU-7).
- `ServerTickMetrics` record methods that nothing calls.
- The queue-cap wording in `PlayerActionHandler`.

**Still open from wave 6:**
- Ids of terrain cubes that get hit reach clients only through full snapshots.
- The id ranges for placed cubes and enemies can overlap.
