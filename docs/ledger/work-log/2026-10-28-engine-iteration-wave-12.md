# Engine Iteration Wave 12: Instanced Fallback Cubes, Delta Actor Snapshots, Headless Server Map, Per-Player Inventory, Receiving-World HUD, Faster Tests

Purpose: Record wave 12, which took the remaining items of the round-2 plan, and its fix loop.

Status: Integrated on `integrate/engine-iteration` and verified by automated tests and Release load runs. Not pushed. Not merged into `master`. This wave ran in continuous mode with the full suite capped at 2 runs in a row, per the user's rule.

## Process

1. Six implementers ran in parallel from `dca4a94`:
   - GPU-6
   - SCALE-2
   - SCALE-7a (headless map and incremental checksum)
   - SCALE-7b (per-player inventory)
   - the receiving-world HUD
   - ARCH-1c

   Identity option D was not included, because the user had not approved it.
2. Integration: all six merges were clean (tip `c1ba599`). The full suite passed in one run: 2,714 tests, 57 s.
3. Verification:
   - The reviewer found no Must fix items, 4 Should fix items and 9 nits.
   - The tester added 6 test files and found no correctness bugs. It found one bandwidth issue: merged snapshots on a slow link always went out as keyframes.
   - The suspected JIT miscompile from SCALE-7a did not reproduce in 3 runtime configurations.
4. Fix loop 1 ran two implementers: delta-actor network fixes, and persistence, checksum and GPU nits. Their merges were clean and gave `ecd4095`.

## Changes

| Task | Change |
| --- | --- |
| GPU-6 | Fallback cubes, dynamic cubes and rubble blocks are drawn as one instanced draw per material group (`WorldInstanceBatchDrawer`). The per-instance matrices go into a persistent `InstanceMatrixBuffer`, using orphan + `SubData` and capacity that only grows. `AnimatedModel` uploads instance matrices once per node. |
| SCALE-2 | Actors go out as delta frames (`ActorReplicationFrame`, `ActorFrameJson`). A field-group bitmask marks what changed. Values are quantized to 1/4096 m and diffed between quantized values, so there is no drift. A keyframe goes out every 64 ticks. A shared broadcast is still encoded once; a connection whose base doesn't match gets the keyframe twin. The client `ActorStateCache` rebuilds full actors and withholds stale ones. The per-connection ack moved into the transport envelope header, so per-broadcast work is O(P) instead of O(P²). Protocol stays at v3, which never shipped. |
| SCALE-7a | The server map is headless: no render descriptors, merged render blocks, render-dirty sets or version bumps. Render exports throw. `WorldStateChecksum` is a running sum (`Map.Checksum.cs`) with the same bits as before, so its cost goes from about 60–160 µs per checksum tick to O(1). Clients benefit too. |
| SCALE-7b | Each player's inventory is loaded in the join prefetch. Nothing is loaded at startup. The item-id counter is seeded from `GetMaxItemInstanceId`. Inventories are evicted along with the departed-profile LRU (1024). `_knownProfileNames` is bounded, and cube owners are tracked in `_cubeOwnerNames`. |
| HUD | While a world transfer runs longer than 300 ms, the HUD shows "Receiving world... N KB, N s" and "Other players are paused until the world arrives". It allocates 0 B per frame, and the per-frame snapshot sizes are unchanged. |
| ARCH-1c | Test-suite changes:<br>• The serial phase dropped from 52 s to 19 s: the 64/32 Hz sessions run concurrently, and only the tests that touch process-wide state stay serial.<br>• More classes are split, and a `ProbeCache` shares the setup probes.<br>• The gate widens after the real-time lane finishes.<br>• A full run takes about 57–62 s, down from about 110 s. |
| Fix: delta network | A merged snapshot for one connection is re-diffed against the last frame that connection was actually sent (`RebaseActorFrame`, `ActorDeltasRebased`). It falls back to a keyframe only when the client holds no frame. `ActorKeyframesForced` is now counted once per frame. Chain breaks are logged. New tests cover an oversized shared delta and a resync between two deltas. |
| Fix: persistence, checksum, GPU | A failed inventory write keeps that player's inventory in memory, or restores it on the next load, instead of evicting it. The inventory docs are rewritten. The unproven JIT comment is replaced with a neutral one, and `SimulationStateChecksum` uses `Map.SumRubblePieceHashes`. `Render` now owns the fallback instance buffer. |

## Measured Impact

| Measurement | Before | After |
| --- | --- | --- |
| Smoke8 server→client per client (Release load) | 144–150 KB/s | 21–25 KB/s |
| Players32 server→client per client | 607–735 KB/s | 300–331 KB/s (the remainder is mostly world mutations) |
| 32 walking clients, actors only (loopback) | about 406 KB/s (old format, estimated) | 28.4 KB/s |
| Merged 8-player snapshots on a slow link | 73,123 B as keyframes | 31,954 B as rebased deltas (forced keyframes 61 → 2) |
| 2,000 fallback cubes in 6 materials | 2,000 draws | 6 draws |
| World checksum per checksum tick (about 21.6k records) | 58–158 µs plus allocations | about 0.002 µs |
| Server map memory, World10k size | 90.1 MB | 83.1 MB (headless) |
| Inventories held after 2,000 distinct players | all 2,000 | 1 active + 1,024 departed |
| Full test suite | about 110 s | about 57–62 s |

Release load on `c1ba599`, Smoke8:
- tick p50 0.55 ms, p99 1.3 ms;
- 64 ticks/s with 0 dropped ticks;
- joins 160 ms.

Release load on `c1ba599`, Players32:
- tick p50 2.9 ms, p99 7.3 ms;
- 64 ticks/s with 0 dropped ticks.

The stall corrections at 32 Hz are unchanged or better than wave 9:

| Stall | Correction |
| --- | --- |
| Send stall | 0.000 m |
| Network stall | 0.000 m |
| Frame stall | 0.835 m |
| Steady and hitchy | 0.464 m |

## Verification

- `dotnet build dream/dream.csproj` finished with 0 errors.
- `dotnet test dream.Tests/dream.Tests.csproj` on `ecd4095` passed 2,732 tests with 25 skipped (gated load and measurement tests), in both of 2 runs, at 59–62 s.
- The tester checked these over loopback:
  - SCALE-2 delta frames:
    - join mid-interval;
    - an actor id reused between keyframes;
    - death and respawn;
    - three resyncs;
    - a slow link with merging and a world transfer;
    - a takeover;
    - a 2,000-tick soak.

    All runs had 0 chain breaks, 0 withheld actors, and a maximum error of 1.22e-4 m, which is the quantization bound.
  - Headless server: join, fracture, resync and late join.
  - SCALE-7b: inventory across a restart and through equip races.
- Not verified:
  - Interactive play.
  - The instanced fallback-cube path on screen. The perf harness view has no dynamic cubes, so this path was checked only structurally and with a recording target.

## Known Limits And Follow-ups

- **GPU-6 on screen.** Check visually by placing or fracturing cubes in game.
- **Older v3 builds.** An older v3 build fails against this server with a NullReferenceException rather than a version mismatch. This is acceptable because v3 never shipped. Bump to v4 before any release.
- **2,000-player churn memory.** It still grows by about 1.4 KB per departed player that the tester could not attribute.
- **Rubble memory.** A `GeometrySubdivisionState` with 1000 unread overrides is created for each rubble cell, about 30 MB at World10k.
- **Fracture LINQ scan.** Each fracture scans all rubble with LINQ (`WorldSessionRuntime.Combat.cs`), although the single-cell `CaptureFracturePiecesInto` exists.
- **Players32 bandwidth.** About 300 KB/s per client is mostly world mutations now, not actors.
- **Wave 13 (the last planned wave):** ARCH-3 step B, which extracts inventory, combat and the persistence bridge from `WorldSessionRuntime` into classes. The items above can go with it.
- **Still waiting on user decisions:**
  - identity option D;
  - the BulletProfilerGuard patch;
  - the ImageSharp advisories;
  - `maxParallelThreads`;
  - the noclip/freeze gate.
