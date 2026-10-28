# Engine Iteration Wave 9: Session Lifecycle, Snapshot Rate, Shader Cost, Incremental Checkpoints, Frozen Resting Cubes, Test Scheduling, Bullet Profiler Guard

Purpose: Record wave 9 of `docs/ledger/plans/2026-10-28-engine-iteration-round-2-plan.md`, as revised at the start of the wave, together with its fix loop.

Status: Integrated on `integrate/engine-iteration`. Verified by automated tests, Release load runs and a 2-client perf-harness run. The user also launched the game from the wave-9 tip. Not pushed. Not merged into `master`. Continuous mode.

## Plan Revision

The round-2 plan was re-checked against what had landed before this wave started:
- NETR-5 was removed from this wave because it already shipped in wave 8.
- ARCH-2 and the replicated-id fixes moved to wave 10. ARCH-2 moves the rules and persistence-port files across layers, and the id fixes share files with SCALE-4.
- Two tasks were added:
  - PHYS-2: player bodies kept cube carpets awake. This was the remaining server-CPU limit from wave 8.
  - ARCH-1b: the test suite had grown back from 35 s to 77 s.

## Process

1. Six implementers ran in parallel from `e8e590f`: NETR-2, NETR-7, GPU-7, SCALE-4, PHYS-2 and ARCH-1b.
2. Integration landed at `81d9a4b`, with no conflicts. The integrator also root-caused and fixed a native crash that still happened at shutdown (see BulletProfilerGuard below).
3. The user paused the iteration, launched the game from the wave-9 tip to check it, and then resumed.
4. Reviews:
   - The reviewer covered both wave 9 and wave 8's fix loop 2, which had not yet been reviewed. Result: no Must fix items, 8 Should fix items.
   - The tester added 7 test files and found 5 issues. One of them was the cause of the late corrections at 32 Hz.
5. Fix loop 1 ran four implementers: session lifecycle, physics wakes plus the guard nits, starved-tick acks, and shutdown/scheduler/docs. Their merges were clean and gave `21d3c09`.
6. The re-review found 2 Must fix items:
   - the integrated server was relaunched for sessions that never used it;
   - an abandoned server kept writing to the database the relaunched server opened.

   It also found 5 Should fix items.
7. Fix loop 2 ran three implementers: local-server relaunch safety, starved-tick accuracy, and the final-checkpoint guarantee. Their merges were clean. This was the second and last fix loop the workflow allows.

## Changes

| Task | Change |
| --- | --- |
| NETR-2 | A client watchdog ends a joined session after 8 s with no inbound data. `Game` then returns to the startup scene through `PlaySessionEndTransition`, which disposes the play scene on the render thread and stops the local server. The startup scene shows "Session ended: <reason>" and does not auto-reconnect. |
| NETR-7 | Frame limits are now set per direction: client→server 64 KB, server→client 32 MB. World10k joins with the default limits. `SnapshotIntervalTicks` defaults to 2 (32 Hz). Mutations, damage and acks carry over into the next snapshot, and action results go out after it. Setting it to 1 gives byte-identical output. |
| GPU-7 | The normal matrix is now computed on the CPU once per draw, and no vertex shader calls `inverse()` any more. Alpha MASK uses `WORLD_ALPHA_MASK` program variants. A `discard` in the shared program doubled GPU actor time even when it never ran, so only the variant has one. GL debug PERFORMANCE messages are logged as info once per id instead of being reported as render faults. |
| SCALE-4 | Checkpoints are now incremental. Dirty sets in `WorldObjectRegistry` and `Map`, plus a value comparison for enemies, decide what is written. A full sweep runs every 12th checkpoint and on dispose. `PersistenceCheckpoint.Merge` uses last-write-wins and keeps tombstones. Tombstones never become SQL deletes; deletes stay on the ordered write queue. |
| PHYS-2 | `CubeRestFreezer` turns a placed cube into a static body after it has rested for 0.5 s. It wakes the cube on a push, when it rides on something moving, when its support is removed, or on `Activate`, and it handles flush seams. The player stays a normal dynamic body. `DREAM_CUBE_REST_FREEZE=0` turns the freezer off. |
| ARCH-1b | A custom xUnit framework (`TestScheduling.cs`) splits 31 long classes into one collection per test. A slot gate of 12 keeps the thread pool free for the tests' own background work. Real-time tests skip the gate and run in their own lane. A join test's time budget is now measured in CPU time, not wall time. |
| BulletProfilerGuard | **Native crash root cause.** BulletSharp 0.11.1's libbulletc is built with `BT_PROFILE` and `BT_THREADSAFE`. Its profiler thread index wraps after 63 threads, and threads that share an index corrupt the shared profile tree. The fix patches `btGetCurrentThreadIndex` in memory to return 64, which disables profiling. The patch applies only when the byte signature matches exactly once, on Windows x64. `DREAM_BULLET_PROFILER_GUARD=0` turns it off. |
| Fix: session lifecycle | The watchdog now refreshes on every non-empty read, so a large resync does not trip it. Returning to the menu keeps the endpoint, and the integrated server is relaunched only on connect. A hung local server is stopped on a background task with a 5 s timeout and then abandoned, and the window close path does the same. |
| Fix: physics | `WakeRidersOf` runs on player remove, park, detach and teleport, and on in-place support moves. The flush tolerance is now relative to coordinate size. The guard writes its patch as one atomic 8-byte exchange. |
| Fix: starved-tick acks | A new optional wire field, `ActorSnapshot.StarvedTicks`, is written only when non-zero, so it adds no bytes in the common case and older readers skip it. The reconciler compares its prediction at ack + k. The harness no longer walks off the world. The snapshot interval override is capped at 16. |
| Fix 2: local server relaunch | The relaunch target is decided before teardown: only a session connected to this game's own integrated server (ready, same port, a loopback host) relaunches it, and the connector dials `127.0.0.1`. `LocalServerShutdownGate` delays any new local-server start until every earlier stop has finished, including abandoned ones, without blocking the render thread; the UI shows "Previous local server is still saving…". Window close waits up to the drain deadline plus `busy_timeout` (about 36 s). `LoopbackHost.IsLoopback` treats localhost, 127.0.0.1 and ::1 as the same host. |
| Fix 2: starved-tick accuracy | Each history sample records its movement intent. A starved snapshot is compared with the client's ack+k sample only when the client's commands match what the server ran; otherwise the client extrapolates, with gravity applied while airborne. `StarvedTicks` now includes the inbox's repeat debt once a skip has started spending it (`GetCarriedRepeatDebtTicks`). |
| Fix 2: final checkpoint | On an unlocked shutdown whose backlog runs past the deadline, the final checkpoint is still written once, after the queue stops. It stays dropped only when the drain saw a lock (`LockSeenDuringShutdownDrain`). `TestScheduling.Split` rolls back a partial split. |
| Fix: shutdown, scheduler, docs | Shutdown now uses one shared drain deadline. With the database locked, shutdown takes 5.7 s instead of 37.1 s. The scheduler's xUnit internals are checked through discovery, and splitting falls back with a diagnostic if they change. `docs/configuration.md` and `docs/architecture.md` now cover the new environment variables, connection liveness, the freezer and the guard. |

## Measured Impact

| Measurement | Before | After |
| --- | --- | --- |
| Smoke8 server tick p50 / p99 | 9.7 / 13.0 ms, 2 ticks per loop | 0.57–0.60 / 0.98–1.19 ms, 1 tick per loop |
| Players32 server tick p50 / p99 | 12.4 / 15.8 ms, 3.1 ticks per loop | 2.7–2.9 / 5.4–6.9 ms, 1 tick per loop |
| Smoke8 active cube bodies with players | about 1,917 | 23–45 p50 |
| World10k checkpoint rows / write time | 14,008 rows, 46.6 ms | about 100 rows, 1.4 ms (incremental) |
| World10k checkpoint capture-tick allocation | about 0.98 MB | 7.8 KB |
| Light8 bytes per client per second | 282–292 KB (64 Hz) | 142–144 KB (32 Hz) |
| Uniform uploads per frame | 258 | 140 |
| Shutdown with the database locked | 37.1 s | 5.7 s |
| Test suite (test time) | 78.6 s | 37–42 s on a quiet machine, about 55 s with the fix-loop tests |
| 1 s send or network stall correction at 32 Hz | 0.093 m (one extra tick), with outliers up to 4.9 m | 0.000 m |
| Native test-host crashes | intermittent | 0 in 5 full runs plus load runs since the guard |

## Verification

- `dotnet build dream/dream.csproj`: 0 errors. The only warnings are the ImageSharp advisories.
- `dotnet test dream.Tests/dream.Tests.csproj` on the fix-loop-2 tip: 2,442 passed and 14 skipped (load and measurement tests that are gated), in 3 out of 3 runs, 69–74 s each.
- Fix-loop-2 stall table (64 Hz and 32 Hz are identical): send stall 0.000 m, frame stall 0.835 m, network stall 0.000 m, steady 200 ms 0.56–0.65 m, hitchy 0.46 m. No snaps in any run.
- Release load at the integration tip: Smoke8 ×3, Players32 and World10k all passed, with 0 dropped ticks at 64 ticks/s, except World10k, which stays limited by simulation.
- 2-client perf harness: 144 FPS, CPU frame time 0.2–0.5 ms, GPU world 0.05 ms, no errors or disconnects.
- The tester also checked:
  - client mirror maps never freeze cubes;
  - pushing a frozen cube matches freezing switched off over loopback;
  - restart equivalence holds with freezing on;
  - 10 local connect and end cycles leak nothing (threads, handles, physics worlds);
  - the guard holds under 16×12 concurrent integrator creations.
- Not verified:
  - Interactive play. The user launched the game themselves.
  - A real MASK asset; none ships.

## Known Limits And Follow-ups

- **Decision for the user, BulletProfilerGuard.** The guard is a runtime patch that covers Windows x64 only. A code-integrity policy such as ACG/HVCI can block it, in which case it logs and leaves the process unprotected. The root-cause fix is a libbulletc built without `BT_PROFILE`, which means owning a native build for win-x64 and linux-x64. The reviewer recommends keeping the guard for now.
- **Abandoned local server.** A hung local server is abandoned after 5 s on session end. Any new local server waits behind it, so the two never write the world database at the same time.
- **"Use Local Server" port field.** If the button is pressed while the previous local server is still saving, the field briefly falls back to port 7777. The server itself waits correctly.
- **Test suite time** rose to about 72 s, because the fix-loop tests added real-time cases. ARCH-1b's scheduler keeps the serial phase short. Profile it again in a later wave.
- **Freezer gap.** A support that is not frozen and is reset in place by `ResetBodyToCenter` does not wake frozen riders. `PhysicsSyncSystem` has no access to the freezer.
- **Join settle correction.** The largest remaining correction, 0.4–0.84 m, happens at join settle, around ack 40, at both snapshot rates.
- **World10k** still cannot keep real time while players are on it.
- **Wave 10:** ARCH-2 (invert Simulation→Server), the replicated-id fixes (terrain cube ids in deltas, overlapping id ranges), SCALE-1 (chunked world transfer), then SCALE-2, ARCH-3/7/8, GPU-3/6, NETR-8, SCALE-7 and ARCH-4.
- **Still waiting on user decisions:** the ImageSharp advisories, `maxParallelThreads`, and the noclip/freeze gate.
