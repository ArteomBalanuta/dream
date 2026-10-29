# Persistence Wave B Baseline Measurements

Purpose: Record the persistence timings at the wave B C0 tip, so the glue commit can check the acceptance rule of `docs/ledger/plans/2026-10-29-wave-b-plan.md`: the measurements stay within C0 x 1.10.

Status: Draft. Measured by workstream B5 on `01da171` ("Merge persistence wave B C0"), before any other wave B workstream merged. C0 adds only contracts and skeletons, so these are the timings of today's SQL code (wave A, schema v2). The wave B work-log replaces this draft after the glue commit re-runs the same tests.

## How To Run

Same method as `2026-10-28-persistence-wave-a-baseline.md`. Release build, `DREAM_LOAD_TEST=1`:

```bash
dotnet build -c Release dream/dream.csproj
DREAM_LOAD_TEST=1 MSYS_NO_PATHCONV=1 dotnet test dream.Tests/dream.Tests.csproj -c Release \
  "/p:BaseOutputPath=<absolute worktree path>/artifacts/verify-bin/" \
  --filter "FullyQualifiedName~PersistenceThroughputMeasurementTests|FullyQualifiedName~IncrementalCheckpointMeasurementTests" \
  --logger "console;verbosity=detailed"
```

Machine: the developer's Windows 11 desktop, .NET 10, Release. Other agents ran builds on the machine at the same time, so single runs are noisy; compare means and treat one-off maxima as noise.

## Results

Two runs, back to back.

### Write queue and join tick

| Measurement | Run 1 | Run 2 |
| --- | --- | --- |
| Write burst: 1000 writes | 27.3 ms, 36,588 writes/s | 23.7 ms, 42,147 writes/s |
| Join tick (4 joins, 150-write backlog), mean / max | 6.4 / 32.5 ms | 6.7 / 34.2 ms |
| Join ticks, each round | 1.7, 32.5, 1.0, 1.2, 1.1, 1.2 | 1.7, 34.2, 1.0, 1.2, 1.2, 1.3 |
| Other ticks, mean / max | 1.1 / 2.9 ms | 1.2 / 3.1 ms |

The second join tick of each run is a consistent outlier (about 33 ms) in both runs.

### Checkpoint capture and write

30 simulated seconds, 20 cubes damaged per second.

**Smoke8** (median tick 0.273 / 0.395 ms in run 1, 0.276 / 0.282 ms in run 2)

| Run | Capture kind | Count | Capture extra ms mean / max | Capture extra bytes mean / max | Rows per write | Write ms mean / max, run 1 | Write ms mean / max, run 2 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| full every capture | full | 6 | 1.399 / 5.154 (run 2: 1.297 / 4.562) | 237,409 / 572,856 | 2401 | 9.18 / 16.26 | 15.00 / 26.02 |
| incremental | full (first / sweep) | 1 | 0.523 (run 2: 0.742) | 572,856 | 2401 | 6.21 | 6.12 |
| incremental | incremental | 5 | 0.580 / 2.728 (run 2: 0.462 / 2.161) | 7,643 / 8,080 | 97.0 | 1.02 / 1.16 | 1.00 / 1.16 |

**World10k** (median tick 1.735 / 1.675 ms in run 1, 1.631 / 1.663 ms in run 2)

| Run | Capture kind | Count | Capture extra ms mean / max | Capture extra bytes mean / max | Rows per write | Write ms mean / max, run 1 | Write ms mean / max, run 2 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| full every capture | full | 6 | 2.163 / 3.745 (run 2: 1.765 / 2.824) | 1,512,155 / 4,189,328 | 14001 | 45.58 / 75.28 | 43.15 / 56.40 |
| incremental | full (first / sweep) | 1 | 1.572 (run 2: 1.703) | 4,189,328 | 14001 | 40.78 | 42.82 |
| incremental | incremental | 5 | 0.113 / 0.133 (run 2: 0.127 / 0.169) | 7,760 / 7,872 | 99.6 | 1.34 / 1.67 | 1.34 / 1.75 |

## Reference Values For The Glue Check

Mean over both runs. The limit is C0 x 1.10.

| Measurement | Baseline | +10% limit |
| --- | --- | --- |
| World10k full checkpoint write, 14001 rows (14 full writes) | 44.0 ms | 48.4 ms |
| World10k incremental checkpoint write, about 100 rows | 1.34 ms | 1.47 ms |
| Smoke8 full checkpoint write, 2401 rows (14 full writes) | 11.2 ms | 12.4 ms |
| Smoke8 incremental checkpoint write, 97 rows | 1.01 ms | 1.11 ms |
| Join tick mean, outliers above 10 ms excluded (10 ticks) | 1.26 ms | 1.39 ms |
| Join tick mean including the outlier | 6.5 ms | 7.2 ms |
| Write burst, 1000 writes | 25.5 ms | 28.1 ms |

For comparison, the wave A figures were World10k full 45.3 ms, incremental 1.27 ms; Smoke8 full 11.7 ms, incremental 1.01 ms; join tick 1.61 ms; burst 31.0 ms. The values above are in the same range. Run-to-run spread on the full writes and the burst exceeds 10%, so re-run at least twice before calling a regression.
