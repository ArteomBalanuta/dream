# Persistence Wave C Baseline Measurements

Purpose: Record the persistence timings at the wave C C0 tip, so the glue commit can check the acceptance rule of `docs/ledger/plans/2026-10-29-wave-c-plan.md`: the measurements stay within C0 x 1.10.

Status: Draft. Measured by workstream D on `fbec55f` ("Merge persistence wave C C0"), before A, B or C merged. C0 only adds the `ObjectId` contracts and shims, so these are the timings of the wave B SQL (schema v4, no triggers). The wave C work-log replaces this draft after the glue commit re-runs the same tests.

## How To Run

Same method as `2026-10-29-persistence-wave-b-baseline.md`. Release build, `DREAM_LOAD_TEST=1`:

```bash
dotnet build -c Release dream/dream.csproj
DREAM_LOAD_TEST=1 MSYS_NO_PATHCONV=1 dotnet test dream.Tests/dream.Tests.csproj -c Release \
  "/p:BaseOutputPath=<absolute worktree path>/artifacts/verify-bin/" \
  --filter "FullyQualifiedName~PersistenceThroughputMeasurementTests|FullyQualifiedName~IncrementalCheckpointMeasurementTests" \
  --logger "console;verbosity=detailed"
```

Machine: the developer's Windows 11 desktop, .NET 10, Release. Three other implementers built and tested on the machine at the same time, so single runs are noisy; compare means and treat one-off maxima as noise.

## Results

Two runs, back to back (all 4 tests passed in both).

### Write queue and join tick

| Measurement | Run 1 | Run 2 |
| --- | --- | --- |
| Write burst: 1000 writes | 23.9 ms, 41,924 writes/s | 24.2 ms, 41,364 writes/s |
| Join tick (4 joins, 150-write backlog), mean / max | 6.5 / 32.5 ms | 6.5 / 32.8 ms |
| Join ticks, each round | 1.7, 32.5, 1.1, 1.0, 1.1, 1.4 | 1.8, 32.8, 1.0, 1.2, 1.1, 1.2 |
| Other ticks, mean / max | 1.2 / 3.0 ms | 1.2 / 3.0 ms |

As in wave B, the second join tick of each run is a consistent outlier (about 33 ms).

### Checkpoint capture and write

30 simulated seconds, 20 cubes damaged per second. "Write" is milliseconds per checkpoint write, mean / max.

**Smoke8** (median tick 0.451 / 0.286 ms in run 1, 0.275 / 0.283 ms in run 2)

| Run | Capture kind | Count | Capture extra ms mean / max | Capture extra bytes mean / max | Rows per write | Write ms, run 1 | Write ms, run 2 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| full every capture | full | 6 | 1.478 / 6.323 (run 2: 1.177 / 4.614) | 267,985 / 644,312 | 2401 | 10.11 / 13.33 | 10.71 / 13.95 |
| incremental | full (first / sweep) | 1 | 0.347 (run 2: 0.750) | 644,312 | 2401 | 5.87 | 4.60 |
| incremental | incremental | 5 | 0.477 / 2.118 (run 2: 0.603 / 2.698) | 8,419 / 8,864 | 97.0 | 0.65 / 0.76 | 0.72 / 0.82 |

**World10k** (median tick 1.691 / 1.630 ms in run 1, 1.654 / 1.641 ms in run 2)

| Run | Capture kind | Count | Capture extra ms mean / max | Capture extra bytes mean / max | Rows per write | Write ms, run 1 | Write ms, run 2 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| full every capture | full | 6 | 1.932 / 3.146 (run 2: 1.929 / 2.808) | 1,721,675 / 4,726,448 | 14001 | 35.54 / 51.50 | 31.51 / 49.85 |
| incremental | full (first / sweep) | 1 | 1.948 (run 2: 1.739) | 4,726,448 | 14001 | 42.79 | 30.30 |
| incremental | incremental | 5 | 0.134 / 0.174 (run 2: 0.141 / 0.153) | 8,557 / 8,672 | 99.6 | 0.99 / 1.54 | 0.98 / 1.66 |

## Reference Values For The Glue Check

Mean over both runs. The limit is C0 x 1.10. As in the wave B reference, "full" is the mean over the 14 full writes (per run, the 6 full-every-capture writes plus the incremental run's first full write).

| Measurement | Baseline | +10% limit |
| --- | --- | --- |
| World10k full checkpoint write, 14001 rows (14 full writes) | 34.0 ms | 37.4 ms |
| World10k incremental checkpoint write, about 100 rows | 0.99 ms | 1.09 ms |
| Smoke8 full checkpoint write, 2401 rows (14 full writes) | 9.6 ms | 10.6 ms |
| Smoke8 incremental checkpoint write, 97 rows | 0.69 ms | 0.76 ms |
| Join tick mean, outliers above 10 ms excluded (10 ticks) | 1.26 ms | 1.39 ms |
| Join tick mean including the outlier | 6.5 ms | 7.2 ms |
| Write burst, 1000 writes | 24.1 ms | 26.5 ms |

These figures are lower than the wave B reference (World10k full 44.0 ms, Smoke8 full 11.2 ms, incremental 1.34 and 1.01 ms, burst 25.5 ms): the machine was less loaded this time. The wave B limits are therefore too loose for wave C. The glue check should not compare against this table alone; it runs two alternating rounds against a C0 build on the same machine at the same time, and that is the gate. This table is a sanity reference.
