# Persistence Wave A Baseline Measurements

Purpose: Record the persistence timings before any wave A workstream merges, so the glue commit can check the acceptance rule of `docs/ledger/plans/2026-10-28-wave-a-plan.md`: checkpoint write time and the join prefetch stay within 10% of this baseline.

Status: Draft. Measured by workstream A5 on the C0 tip `b145a9f` ("Merge persistence wave A C0"), before A5 or any other workstream merged. C0 adds only contracts and skeletons, which nothing on these paths calls yet, so these are the timings of today's SQL code. The wave A work-log replaces this draft after the glue commit re-runs the same tests.

## How To Run

The tests are gated like the load harness: `[LoadFact]` skips them unless `DREAM_LOAD_TEST=1`. Run them in Release. A Release test build copies native files from `dream/bin/Release`, so build the game in Release first:

```bash
dotnet build -c Release dream/dream.csproj
DREAM_LOAD_TEST=1 MSYS_NO_PATHCONV=1 dotnet test dream.Tests/dream.Tests.csproj -c Release \
  "/p:BaseOutputPath=<absolute worktree path>/artifacts/verify-bin/" \
  --filter "FullyQualifiedName~PersistenceThroughputMeasurementTests|FullyQualifiedName~IncrementalCheckpointMeasurementTests" \
  --logger "console;verbosity=detailed"
```

The numbers are printed as test output. `PersistenceThroughputMeasurementTests` also appends them to `artifacts/load/persistence-write-burst.txt` and `persistence-join-tick.txt`.

"Join prefetch timings" means `PersistenceThroughputMeasurementTests.JoinTick_WithAWriteBacklog`. It times the tick that applies a batch of 4 deferred joins while the write queue holds a 150-write backlog; the prefetch reads the profile, world state and inventory off the tick. The `JoinPersistencePrefetchTests` in the normal suite assert behaviour, not timings.

Machine: the developer's Windows 11 desktop, 16 logical processors, .NET 10.0.9, Release. Other work ran on the machine at the same time, so single runs are noisy: compare means over several runs, and treat one-off maxima (a single 45 ms tick) as noise.

## Results

Two runs, back to back.

### Write queue and join tick (`PersistenceThroughputMeasurementTests`)

| Measurement | Run 1 | Run 2 |
| --- | --- | --- |
| Write burst: 1000 mixed writes (profile upsert, cube upsert, cube delete) | 24.8 ms, 40,267 writes/s | 39.2 ms, 25,503 writes/s |
| Join tick (4 joins, 150-write backlog), mean / max | 9.2 / 47.8 ms | 2.3 / 3.6 ms |
| Join ticks, each round | 2.4, 47.8, 1.1, 1.3, 1.2, 1.2 | 3.6, 3.0, 1.1, 3.1, 1.6, 1.3 |
| Other ticks, mean / max | 1.3 / 3.0 ms | 3.7 / 44.3 ms |

Excluding the single outlier tick in each run, a join tick costs 1 to 4 ms.

### Checkpoint capture and write (`IncrementalCheckpointMeasurementTests`)

30 simulated seconds at 64 ticks/s, 20 cubes damaged per second. "Full every capture" forces a full checkpoint on every capture; "incremental" is the shipped setting (a full sweep every 12th capture).

**Smoke8** (median tick 0.277 / 0.285 ms in run 1, 0.438 / 0.396 ms in run 2)

| Run | Capture kind | Count | Capture tick extra ms mean / max | Capture tick extra bytes mean / max | Rows per write | Write ms mean / max, run 1 | Write ms mean / max, run 2 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| full every capture | full | 6 | 1.761 / 7.595 (run 2: 2.088 / 7.516) | 237,889 / 574,176 | 2404 | 8.50 / 11.80 | 15.85 / 27.82 |
| incremental | full (first / sweep) | 1 | 0.482 (run 2: 0.524) | 574,176 | 2404 | 6.27 | 9.71 |
| incremental | incremental | 5 | 0.661 / 2.868 (run 2: 1.008 / 4.460) | 7,677 / 8,248 | 97.0 | 1.15 / 1.49 | 1.39 / 1.50 |

**World10k** (median tick 2.619 / 2.644 ms in run 1, 2.616 / 2.570 ms in run 2)

| Run | Capture kind | Count | Capture tick extra ms mean / max | Capture tick extra bytes mean / max | Rows per write | Write ms mean / max, run 1 | Write ms mean / max, run 2 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| full every capture | full | 6 | 2.054 / 3.311 (run 2: 1.978 / 3.316) | 1,513,553 / 4,194,080 | 14008 | 70.17 / 116.03 | 56.67 / 87.24 |
| incremental | full (first / sweep) | 1 | 1.017 (run 2: 1.920) | 4,194,080 | 14008 | 61.13 | 88.78 |
| incremental | incremental | 5 | -0.250 / 0.253 (run 2: -0.116 / 0.239) | 7,859 / 8,368 | 99.6 | 2.23 / 3.11 | 1.50 / 2.14 |

The capture rows (tick cost and bytes) belong to the tick thread and should not move in wave A. The write columns are what wave A touches: A4 moves the checkpoint SQL into the repositories and adds the `updated_utc` guard.

## Reference Values For The Glue Check

Use the mean over both runs, and re-run at least twice before calling a regression:

| Measurement | Baseline | +10% limit |
| --- | --- | --- |
| World10k full checkpoint write, 14008 rows (all 14 full writes) | 65.1 ms | 71.6 ms |
| World10k incremental checkpoint write, about 100 rows | 1.87 ms | 2.05 ms |
| Smoke8 full checkpoint write, 2404 rows (all 14 full writes) | 11.6 ms | 12.7 ms |
| Smoke8 incremental checkpoint write, 97 rows | 1.27 ms | 1.40 ms |
| Join tick mean, outliers above 10 ms excluded (11 ticks) | 1.9 ms | 2.1 ms |
| Write burst, 1000 writes | 32.0 ms | 35.2 ms |

The run-to-run spread is already larger than 10% for the full writes and the write burst, so a result above a limit on one run is not by itself a regression.
