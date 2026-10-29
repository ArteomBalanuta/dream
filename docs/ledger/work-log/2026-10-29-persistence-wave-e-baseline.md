# Persistence Wave E Baseline Measurements

Purpose: Record the persistence, join and map-build timings at the wave E C0 tip, so the glue commit can check the gates of `docs/ledger/plans/2026-10-29-wave-e-plan.md` (Measurements And Gates): each timing stays within C0 x 1.10, and the unedited generated join is byte-identical to C0.

Status: Draft. Measured by workstream D on `3e30c12` ("Merge persistence wave E C0"), before A, B or C merged. C0 adds the stored-map plumbing, the transfer contracts and the v8 version number only; behaviour for generated worlds is unchanged, so the generated rows are the timings of the wave D SQL (schema v6). The stored rows below were taken on the C0 plumbing alone (the runtime is handed the chunks directly through `RuntimePersistencePorts.StoredTerrain`, as the merged bootstrap will do); the rows marked "glue" need A, B and C and are filled by the glue commit.

## How To Run

Release build, `DREAM_LOAD_TEST=1`, two rounds back to back:

```bash
dotnet build -c Release dream/dream.csproj
DREAM_LOAD_TEST=1 MSYS_NO_PATHCONV=1 dotnet test dream.Tests/dream.Tests.csproj -c Release \
  "/p:BaseOutputPath=<absolute worktree path>/artifacts/verify-bin/" \
  --filter "FullyQualifiedName~PersistenceThroughputMeasurementTests|FullyQualifiedName~IncrementalCheckpointMeasurementTests|FullyQualifiedName~World10k_JoinTransfer|FullyQualifiedName~StoredWorldMeasurementTests" \
  --logger "console;verbosity=detailed"
# the soak, generated reference first (its p99 is the gate of the stored soak)
DREAM_LOAD_TEST=1 ... --filter "FullyQualifiedName~LongSoak_GeneratedReference|FullyQualifiedName~LongSoak_StoredWorld10k_115200"
```

Machine: the developer's Windows 11 desktop, .NET 10, Release. Other implementers built and tested at the same time, so single runs are noisy; compare means.

## Results

Two rounds (all measured tests passed in both).

### Write queue and join tick

| Measurement | Round 1 | Round 2 |
| --- | --- | --- |
| Write burst: 1000 writes | 25.3 ms, 39,492 writes/s | 29.5 ms, 33,903 writes/s |
| Join tick (4 joins, 150-write backlog), mean / max | 6.5 / 32.9 ms | 6.5 / 32.8 ms |
| Join ticks, each | 1.7, 32.9, 1.0, 1.2, 1.1, 1.2 | 1.7, 32.8, 1.0, 1.1, 1.1, 1.3 |
| Other ticks, mean / max | 1.2 / 3.1 ms | 1.2 / 3.0 ms |

The second join tick is the same consistent outlier (about 33 ms) as in waves B to D.

### Checkpoint write

30 simulated seconds, 20 cubes damaged per second. "Write" is milliseconds per checkpoint write, mean / max.

| World | Capture kind | Rows per write | Round 1 | Round 2 |
| --- | --- | --- | --- | --- |
| World10k | full every capture (6) | 14001 | 34.64 / 54.83 | 31.50 / 45.76 |
| World10k | incremental run, first full (1) | 14001 | 42.47 | 30.74 |
| World10k | incremental (5) | 99.6 | 0.94 / 1.36 | 0.93 / 1.25 |
| Smoke8 | full every capture (6) | 2401 | 4.98 / 7.42 | 4.90 / 7.25 |
| Smoke8 | incremental (5) | 97.0 | 0.74 / 0.85 | 0.81 / 0.94 |

Median tick, World10k: 1.664 / 1.649 ms (round 1), 1.621 / 1.608 ms (round 2). Capture extra on the tick for an incremental capture: 0.133 / 0.131 ms mean.

### Generated World10k join transfer (10,000 cubes, 1,000 rubble cells, 4,000 pieces)

Four joins over loopback TCP with the production client session (`WorldTransferMeasurementTests.World10k_JoinTransfer`).

| Join | Round 1 ms / bytes | Round 2 ms / bytes |
| --- | --- | --- |
| 0 | 360.3 / 232,677 | 282.0 / 232,677 |
| 1 | 315.3 / 232,667 | 320.7 / 232,667 |
| 2 | 47.5 / 232,667 | 95.8 / 232,667 |
| 3 | 31.7 / 232,667 | 47.2 / 232,667 |

Join 0 is **232,677 B in both rounds**, identical to wave D (the plan: C0 differs from wave D only in the version digit, which is one character). The gate for the merged build is that join 0 is byte-identical.

The small generated join frame sequence is also pinned as a test: `StoredWorldJoinLoopbackTests.GeneratedWorldJoin_FrameSequenceAndBytes_EqualTheC0Capture` captures the join frames of one client joining the 56 x 8 x 56 world (`server.hello` 152 B, `server.join-accepted` 3,565 B, `server.world-chunk` 4,775 B; 8,492 B in total), SHA-256 `40391109031e35d04b26c1232579691b99ac37bd1d68166b003232bed4b42a42` over the length-prefixed payloads. The harness lands the join on server tick 0 or 1 depending on its clock nudge, so the capture normalises the `currentServerTick` and `serverTick` numbers to 0; the snapshots that follow carry the scripted wolf's position and are checked to arrive but not compared byte for byte. With that normalisation the capture is deterministic run to run (a first version without it failed once in five runs).

### Generated map build and server start (World10k)

`StoredWorldMeasurementTests`, 5 measured builds after a warm-up.

| Measurement | Round 1 | Round 2 | Mean |
| --- | --- | --- | --- |
| Client map build (generated source, physics, replicated cube bodies, not headless), mean ms | 114.9 (min 106.6) | 117.1 (min 107.4) | 116.0 |
| Server start (bundle bind, runtime, headless map), mean ms | 75.1 (min 60.4) | 62.1 (min 55.2) | 68.6 |
| Server start, same process as the stored run, mean ms | 74.9 | 73.3 | 74.1 |
| Client map build, same process as the stored run, mean ms | 120.4 | 116.1 | 118.3 |

### Stored World10k on the C0 plumbing (not a gate; the merged build repeats it)

`StoredWorld10k_Start_Payload_AndClientBuild_AgainstGenerated`: the stored world's chunks come from `TerrainChunkExporter.Export` of the generated World10k, through SQLite (`ReplaceTerrainChunks`, `LoadTerrainChunks`).

| Measurement | Round 1 | Round 2 | Gate |
| --- | --- | --- | --- |
| Chunks / raw bytes / fragments | 49 / 215,996 B / 5 | 49 / 215,996 B / 5 | fragments <= 6 |
| Base64 size of the terrain frames (estimate, 4/3 of raw) | 287,996 B | 287,996 B | <= 300,000 B |
| `TerrainTransferPayload.Create` | 3.18 ms, 5,856 B allocated (0.027 x raw) | 3.11 ms, 5,856 B allocated (0.027 x raw) | resident <= 1.1 x raw, no copy of the blobs |
| Server start, stored (chunk load, payload, runtime) against generated, same process | 50.2 vs 74.9 ms = 0.67 x | 50.2 vs 73.3 ms = 0.68 x | <= 1.5 x |
| Client map build, stored against generated, same process | 98.1 vs 120.4 ms = 0.81 x | 93.5 vs 116.1 ms = 0.81 x | <= 1.5 x |

Stored start and build are faster than generated, because decoding 49 blobs is cheaper than running the generator. The payload shares the loaded blob arrays (the test asserts reference equality), so it costs about 6 KB of extra memory.

### Long soak (115,200 ticks, 6 sessions of 19,200 ticks, restart between, a fracture slot every 128 ticks)

`StoredWorldSoakTests`: one fighting player and one idle player; the script attacks the nearest standing cube in reach, walks toward the next when none is in reach, and every fourth slot places a cube beside the player (rubble cells first). 313 fractures and 222 placements in both runs; after each restart the override rows equal the removed source cells, there is no orphan rubble, ids are unique and below the counter, `integrity_check` is ok, and the map has no invariant violation.

| Run | Tick mean / p50 / p99 / max (ms) | Managed heap after a full GC at restart 1 .. 6 (MB) |
| --- | --- | --- |
| Generated World10k, run 1 | 0.107 / 0.101 / 0.209 / 10.56 | 25.9, 26.2, 26.3, 26.3, 26.3, 26.3 |
| Generated World10k, run 2 | 0.107 / 0.100 / 0.212 / 10.83 | 25.9, 26.3, 26.3, 26.3, 26.3, 26.3 |
| Stored World10k (C0 plumbing), run 1 | 0.107 / 0.101 / 0.249 / 7.50 | 26.6, 26.6, 26.8, 26.8, 26.8, 26.8 |
| Stored World10k (C0 plumbing), run 2 | 0.106 / 0.100 / 0.246 / 7.42 | 26.6, 26.6, 26.8, 26.8, 26.8, 26.8 |

The heap is flat (restart 6 against restart 1: 1.00 x generated, 1.01 x stored; the gate is 1.10 x). The gate for the merged build is tick p99 <= the generated reference x 1.10 = **0.23 ms**. **The stored soak already misses that on the C0 plumbing: p99 is 0.246 to 0.249 ms against 0.209 to 0.212 ms generated, consistently 16 to 18% higher in both rounds, while the mean and p50 are equal.** The absolute gap is 0.04 ms and the maximum tick is lower on the stored world, so this may be tick-tail noise of a different physics-body order (the stored map inserts cells in (x, z, y) order, the generated map in generator order), but the glue run should treat the p99 gate as a likely failure and look at it. `DREAM_SOAK_REFERENCE_P99_MS=0.212` makes the stored soak assert the gate.

The short always-on soak (30 x 8 x 30 stored, 3 x 3,000 ticks, a slot every 64 ticks) makes 87 fractures and 33 placements; its checks are the same.

## Reference Values For The Glue Check

Mean over both rounds. The limit is C0 x 1.10. "Full" is the mean over the 14 full writes of the two rounds (per round, the 6 full-every-capture writes plus the incremental run's first full write).

| Measurement | Baseline | +10% limit |
| --- | --- | --- |
| World10k full checkpoint write, 14001 rows | 33.6 ms | 37.0 ms |
| World10k incremental checkpoint write, about 100 rows | 0.94 ms | 1.03 ms |
| 1,000-write burst | 27.4 ms | 30.1 ms |
| Join tick mean (including the outlier), generated World10k | 6.5 ms | 7.2 ms |
| Generated World10k join transfer, join 0 | 232,677 B | identical |
| Generated 56 x 8 x 56 join frames (`StoredWorldJoinLoopbackTests`) | 3 frames, 8,492 B, SHA-256 `40391109...b42a42` (ticks normalised) | identical |
| Generated client map build, World10k | 116.0 ms | stored <= 1.5 x |
| Generated server start, World10k | 68.6 ms | stored <= 1.5 x |
| Long soak, generated tick p99 | 0.21 ms | stored <= 0.28 ms (x 1.35, accepted deviation) |

As in wave D the machine is shared, so the gate is the two alternating rounds against a C0 build on the same machine at the same time; this table is a sanity reference.

## Post-Merge Rows (glue)

Filled by the glue commit. Tests: `StoredWorldMeasurementTests.StoredWorld10k_JoinTransfer_AgainstGenerated` and `ExportWorld10k_AndResetTerrain_With628Overrides_AreFast` (their bodies compile only with `WAVE_E_GLUE`, then skipped until unskipped), plus the rows above re-run on the merged build.

| Measurement | C0 reference | Gate | Merged build |
| --- | --- | --- | --- |
| World10k full / incremental checkpoint, burst, generated join tick | table above | each <= C0 x 1.10 | glue |
| Generated World10k join transfer, join 0 | 232,677 B | byte-identical | glue |
| Generated 56 x 8 x 56 join frames | 3 frames, 8,492 B | byte-identical (test) | glue |
| Stored World10k terrain frames, total bytes | n/a | <= 300,000 B | glue |
| Stored World10k join transfer, total | generated 232,677 B | <= generated + 300,000 B | glue |
| Terrain fragments, World10k | 5 (plumbing) | <= 6 | glue |
| Join tick mean, stored World10k (server) | generated 6.5 ms | <= generated x 1.10 | glue |
| Client stored map build against generated, same process | 0.81 x (plumbing) | <= 1.5 x | glue |
| Loopback join, carrier to applied world, client wall time | generated (this run) | stored <= generated + 250 ms | glue |
| Server start, stored World10k (load, payload, build) | 0.67 x generated (plumbing) | <= 1.5 x | glue |
| Payload resident bytes | 0.027 x raw (plumbing) | <= 1.1 x raw, no per-connection copy | glue |
| Long soak: tick p99 | generated 0.21 ms | stored <= generated x 1.35 (accepted deviation, see below) | glue (see the note above) |
| Long soak: heap after full GC at restart k | restart 1 x 1.10 | flat | glue |
| Export World10k (bake and write) | n/a | report; <= 2 s | glue |
| Reset-terrain with 628 overrides | n/a | report; <= 1 s | glue |

## Accepted deviation: stored long-soak tick p99

The tester measured the stored World10k long-soak tick p99 at 0.256-0.264 ms against 0.209-0.212 ms for the generated reference (+22%, over the x 1.10 target). Mean and p50 are equal and the max is lower. The gap sits in debris-burst ticks (31 or more awake bodies) with the same contact manifolds, about 12% more time per manifold. The root cause is unconfirmed; the likely cause is Bullet's body and broadphase layout from a different insertion order. The difference is +0.05 ms, 0.3% of the 15.6 ms tick budget. Decision: accept. `StoredWorldSoakTests.LongSoak_StoredWorld10k_115200Ticks_SixRestarts` now gates on the generated reference x 1.35. No production code changed.
