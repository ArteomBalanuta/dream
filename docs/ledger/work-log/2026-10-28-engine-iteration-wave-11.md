# Engine Iteration Wave 11: Large-File Splits, Doc Drift, Bounded Session State, Flaky-Test Fixes

Purpose: Record wave 11 of the round-2 plan and its fix loop. This was a refactor-only wave, run on its own because splitting the three largest files touches what every other task touches.

Status: Integrated on `integrate/engine-iteration`. Not pushed. Not merged into `master`. Continuous mode.

## Process

1. Five implementers ran in parallel from `35a6960`: ARCH-3, ARCH-7, ARCH-8, ARCH-4 and SCALE-7. All five merges were clean, and ARCH-3 merged last. The tip was `97cdb9f`; the full suite passed 3 of 3 runs, 2,612 tests.
2. Verification:
   - The reviewer found 0 Must fix items, 2 Should fix items and 5 nits.
   - The tester found no performance regression, measured A/B against wave 10 on the same machine.
   - The tester also diagnosed every test that had failed intermittently across waves 8–11.
3. Fix loop 1 ran two implementers: runtime maps, docs and nits; and flaky tests plus disconnect noise. Their merges were clean.
4. **Test-run limit.** During this wave the user capped full-suite verification at 2 runs in a row, never more. Flaky tests are now rerun in isolation instead of repeating the full suite.

## Changes

| Task | Change |
| --- | --- |
| ARCH-3 | `WorldSessionRuntime` (3,296 lines) is split into 13 partial files. 213 members are byte-identical, confirmed by SHA. The files are Sessions, PlayerTargets, Tick, Commands, Combat, Building, Enemies, Replication, WorldSnapshot, Inventory, Persistence and Checkpoints, plus the main file. |
| ARCH-7 | `Render` (1,640 lines) and `PlayScene` (988 lines) are now 8 partial files each, with every body byte-identical. A new `PartialClassSource` test helper reads the union of a class's partials, and 10 source-scan tests now use it with their assertions unchanged. The client allowlist entry moved to `PlayScene.Render.cs`. |
| ARCH-8 | `Map` (2,305 lines) is split into 11 partial files, with 114 members byte-identical. The microcell merger stays a partial because it reads `Map` state. |
| ARCH-4 | Doc drift fixed across 11 docs and SKILL.md: .NET 10, 11 `DREAM_*` variables that were undocumented, the client session lifecycle, the persistence write path, rendering, and testing. It also records that `DREAM_CLIENT_SESSION_ID` is parsed but ignored by the desktop client, and that the server always listens on port 5057. |
| SCALE-7 (a, b) | `PlayerSessionRegistry` keeps only active sessions plus an LRU of 1,024 departed ones. Iteration is allocation-free, and `HasSpawnedRemotePlayer` is O(1). `NetworkDiagnosticsRateLimiter` keys now expire, with a hard cap of 4,096. |
| Fix: runtime maps, docs | `_sessionInteractionModes` entries are removed when the registry forgets a session (`SessionForgotten`). `_profileCache` keeps active players plus an LRU of 1,024 departed names, and no saved data is lost. Line references broken by the splits are fixed; docs now cite member names rather than line numbers. Several comment nits are fixed. |
| Fix: flaky tests, disconnect noise | Each long-running flaky test was fixed at its root cause, with no assertion or budget loosened:<br>• `TickPacer`: best of 3 measurements.<br>• 32 Hz steady-play test: its own `TimingSensitive` class.<br>• Lock-retry test: waits for the worker to start.<br>• Liveness test: flushes before each clock jump.<br>• Production: the hello wait now uses the injected `TimeProvider`.<br>• Production: `InboundFrameCount` counts processed frames only.<br>• Production: a normal client disconnect (connection reset) is no longer counted as a connection error, and the client sends a FIN before closing. |

## Measured Impact

| Measurement | Before | After |
| --- | --- | --- |
| Largest source file | 3,296 lines (`WorldSessionRuntime`) | 695 lines (`WorldSessionRuntime.Persistence.cs`) |
| Registry after 10k join/leave cycles | 10,009 sessions walked per tick | 9 |
| Median tick after 10k cycles | 0.072 → 0.126 ms | 0.072 → 0.072 ms |
| Time for 10k join/leave cycles | 9.84 s | 0.76 s |
| Spurious connection errors from normal disconnects | about 47 per 260 churn disconnects | 0 |
| Smoke8 tick and join (A/B on the same machine) | p50 0.55–0.70 / p99 1.32–1.85 ms, joins 168–172 ms (wave 10) | p50 0.63 / p99 1.45–1.52 ms, joins 167–171 ms (no regression) |

## Verification

- `dotnet build dream/dream.csproj`: 0 errors. The warnings are the ImageSharp advisories plus 3 CS860x warnings that predate this wave.
- Wave tip before the fix loop: 2,612 passed, 3 of 3 runs.
- Each fix branch on its own: fix A passed 2,619 in 3 of 3 runs, fix B passed 2,618 in 4 of 4 runs.
- The merged fix-loop tip passed 2,622 tests with 19 skipped (gated load and measurement tests), in 2 out of 2 runs, about 110 s each.
- The tester checked:
  - 200 real loopback connect/disconnect cycles stayed bounded;
  - a returning session reclaimed its id;
  - Release load numbers and the 2-client perf harness were unchanged: programs 6, uniforms 105.
- Not verified: interactive play.

## Known Limits And Follow-ups

- **Inventory state** (`_inventorySlotsByPlayer`, `_itemInstances`) is still loaded whole at startup and never evicted. Bounding it needs per-player loads in the persistence repositories.
- `_knownProfileNames` still grows with every distinct name.
- **Test suite time** is about 90–100 s. A profiling pass is due.
- **Next (wave 12):**
  - GPU-6: instanced fallback cubes
  - SCALE-2: delta actors
  - SCALE-7 remainder: headless map flag, incremental checksum, per-player inventory
  - a "receiving world" HUD state
  - test-suite profiling
  - identity option D, if the user approves it
- **Still waiting on user decisions:**
  - identity option D
  - the BulletProfilerGuard patch
  - the ImageSharp advisories
  - `maxParallelThreads`
  - the noclip/freeze gate
