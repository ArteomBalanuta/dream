# Persistence Wave D Baseline Measurements

Purpose: Record the persistence and join timings at the wave D C0 tip, so the glue commit can check the acceptance rule of `docs/ledger/plans/2026-10-29-wave-d-plan.md`: each timing stays within C0 x 1.10, and the transfer bytes of an unedited join are byte-identical to C0.

Status: Draft. Measured by workstream D on `ca5d7d3` ("Merge persistence wave D C0"), before A, B or C merged. C0 adds the map-source seam, the terrain contracts and shims only, so these are the timings of the wave C SQL (schema v5, 4 object-id triggers). The wave D work-log replaces this draft after the glue commit re-runs the same tests; rows marked "glue" are filled then.

## How To Run

Same method as `2026-10-29-persistence-wave-c-baseline.md`. Release build, `DREAM_LOAD_TEST=1`:

```bash
dotnet build -c Release dream/dream.csproj
DREAM_LOAD_TEST=1 MSYS_NO_PATHCONV=1 dotnet test dream.Tests/dream.Tests.csproj -c Release \
  "/p:BaseOutputPath=<absolute worktree path>/artifacts/verify-bin/" \
  --filter "FullyQualifiedName~PersistenceThroughputMeasurementTests|FullyQualifiedName~IncrementalCheckpointMeasurementTests|FullyQualifiedName~World10k_JoinTransfer|FullyQualifiedName~TerrainMeasurementTests" \
  --logger "console;verbosity=detailed"
```

Machine: the developer's Windows 11 desktop, .NET 10, Release. Other implementers built and tested on the machine at the same time, so single runs are noisy; compare means and treat one-off maxima as noise.

## Results

Two runs, back to back (all 5 tests passed in both). `GeneratedWorld10k_BuildReference` was run separately, twice.

### Write queue and join tick

| Measurement | Run 1 | Run 2 |
| --- | --- | --- |
| Write burst: 1000 writes | 26.6 ms, 37,544 writes/s | 26.1 ms, 38,279 writes/s |
| Join tick (4 joins, 150-write backlog), mean / max | 6.8 / 34.4 ms | 6.5 / 32.7 ms |
| Join ticks, each round | 1.8, 34.4, 1.0, 1.1, 1.3, 1.2 | 1.8, 32.7, 1.1, 1.0, 1.2, 1.2 |
| Other ticks, mean / max | 1.2 / 3.9 ms | 1.1 / 3.0 ms |

As in waves B and C, the second join tick of each run is a consistent outlier (about 33 ms).

### Checkpoint capture and write

30 simulated seconds, 20 cubes damaged per second. "Write" is milliseconds per checkpoint write, mean / max.

**Smoke8** (median tick 0.236 / 0.234 ms in run 1, 0.238 / 0.233 ms in run 2)

| Run | Capture kind | Count | Capture extra ms mean / max | Capture extra bytes mean / max | Rows per write | Write ms, run 1 | Write ms, run 2 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| full every capture | full | 6 | 1.148 / 4.958 (run 2: 1.193 / 5.467) | 267,975 / 644,248 | 2401 | 5.10 / 8.30 | 5.08 / 8.77 |
| incremental | full (first / sweep) | 1 | 0.256 (run 2: 0.229) | 644,248 | 2401 | 4.60 | 4.58 |
| incremental | incremental | 5 | 0.460 / 2.196 (run 2: 0.475 / 2.273) | 8,403 / 8,784 | 97.0 | 0.67 / 0.81 | 0.67 / 0.86 |

**World10k** (median tick 1.611 / 1.670 ms in run 1, 1.627 / 1.642 ms in run 2)

| Run | Capture kind | Count | Capture extra ms mean / max | Capture extra bytes mean / max | Rows per write | Write ms, run 1 | Write ms, run 2 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| full every capture | full | 6 | 1.809 / 2.950 (run 2: 1.781 / 2.720) | 1,721,664 / 4,726,384 | 14001 | 34.60 / 68.56 | 32.69 / 55.51 |
| incremental | full (first / sweep) | 1 | 1.871 (run 2: 1.798) | 4,726,384 | 14001 | 49.80 | 30.70 |
| incremental | incremental | 5 | 0.108 / 0.122 (run 2: 0.152 / 0.183) | 8,558 / 8,680 | 99.6 | 0.96 / 1.45 | 0.98 / 1.42 |

### Unedited join transfer (World10k, 10,000 cubes, 1,000 rubble cells, 4,000 pieces)

Four joins over loopback TCP with the production client session, one at a time (`WorldTransferMeasurementTests.World10k_JoinTransfer`).

| Join | Run 1 ms / bytes | Run 2 ms / bytes |
| --- | --- | --- |
| 0 | 346.5 / 232,677 | 298.8 / 232,677 |
| 1 | 317.7 / 232,667 | 300.2 / 232,667 |
| 2 | 47.2 / 232,923 | 40.1 / 232,667 |
| 3 | 31.9 / 232,667 | 55.0 / 232,667 |

Join 0 is byte-identical in both runs (232,677 B), and 232,667 B is the floor; the other joins differ by a few bytes with the snapshot tick. `ExportWorldSnapshot` (no release) 0.53 / 0.64 ms, 983,656 B allocated; single-frame `JoinAccepted` encode 2.33 / 2.31 ms, 228,000 B. Cubes / rubble received: 10,221 / 1,000. (The tick-stall column of this test compares against a median tick of 80 to 88 ms, which is a quirk of how the test drives the tick; it is not a gate.)

### Generated map build (reference for the stored-map budget)

`TerrainMeasurementTests.GeneratedWorld10k_BuildReference`: `new Map(GeneratedMapSource(100x8x100), physics, headless: true)`, 7 builds after a warm-up.

| Run | Mean ms | Min ms | Max ms | `CreateCells` alone |
| --- | --- | --- | --- | --- |
| 1 | 161.9 | 58.4 | 323.7 | 39.0 ms |
| 2 | 229.0 | 89.5 | 674.8 | 34.0 ms |

This one is very noisy (Bullet body creation and GC dominate). For the 1.5x check, the glue run must take the generated and the stored build back to back in the same process, as `StoredWorld10k_ChunkBytes_EncodeReplaceAndLoadPlusBuild` does.

## Reference Values For The Glue Check

Mean over both runs. The limit is C0 x 1.10. "Full" is the mean over the 14 full writes (per run, the 6 full-every-capture writes plus the incremental run's first full write).

| Measurement | Baseline | +10% limit |
| --- | --- | --- |
| World10k full checkpoint write, 14001 rows (14 full writes) | 34.6 ms | 38.1 ms |
| World10k incremental checkpoint write, about 100 rows | 0.97 ms | 1.07 ms |
| Smoke8 full checkpoint write, 2401 rows (14 full writes) | 5.0 ms | 5.5 ms |
| Smoke8 incremental checkpoint write, 97 rows | 0.67 ms | 0.74 ms |
| Join tick mean, outliers above 10 ms excluded (10 ticks) | 1.2 ms | 1.3 ms |
| Join tick mean including the outlier | 6.65 ms | 7.3 ms |
| Write burst, 1000 writes | 26.35 ms | 29.0 ms |
| Unedited join transfer bytes (join 0) | 232,677 B | identical |

The machine was less loaded for Smoke8 than in wave C (5.0 ms against 9.6 ms), so the gate is the two alternating rounds against a C0 build on the same machine at the same time; this table is a sanity reference.

## Post-Merge Rows (glue)

Filled by the glue commit. Tests: `TerrainMeasurementTests.World10k_With1000SeededOverrides_RestoreJoinAndTransfer` and `StoredWorld10k_ChunkBytes_EncodeReplaceAndLoadPlusBuild` (both compiled out until `WAVE_D_GLUE` is defined, then skipped until unskipped).

| Measurement | C0 reference | Target | Merged build |
| --- | --- | --- | --- |
| Restore with 1,000 seeded overrides (host creation, ms) | glue | report | glue |
| Join with 1,000 overrides (ms) | glue | report | glue |
| Join transfer bytes with 1,000 overrides | 232,677 B (unedited) | report; client checksum matches | glue |
| Unedited join transfer bytes after merge | 232,677 B | identical | glue |
| World10k full / incremental checkpoint, burst, join tick | table above | <= C0 x 1.10 | glue |
| Stored map: chunk count (100x100 columns, 16 x 16) | 49 expected (7 x 7) | report | glue |
| Stored map: blob bytes (and x 4/3 as base64, E1's wire budget) | n/a | <= 262,144 B | glue |
| Stored map: encode time (`TerrainChunkExporter.Export`), ms | n/a | report | glue |
| `ReplaceTerrainChunks` time, ms | n/a | report | glue |
| Stored map load + build against the generated build | generated build, see above | <= 1.5x | glue |
