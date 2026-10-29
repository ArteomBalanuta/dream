# Persistence Wave B: Worlds As Rows

Purpose: this log records persistence wave B (Phase 2 of `docs/ledger/plans/2026-10-28-world-persistence-roadmap.md`) and its fix loop.

Status: integrated on `integrate/engine-iteration` and verified by automated tests, Release measurements, real legacy databases and the perf harness. Not pushed. Not merged into `master`. Interactive play has not been checked.

## Process

1. **Plan.** The architect wrote `docs/ledger/plans/2026-10-29-wave-b-plan.md`, settling these questions:
   - Repositories are bound to one world each.
   - Each per-world table gets a `(world_id, local id)` key.
   - Inventory and starter grants are per world.
   - Seed and world time move onto the `worlds` row.
   - "Buildings outlive their builder" is deferred to wave C.
2. **C0 (`01da171`).** The shared contracts: `WorldId`, `WorldDescriptor`, `WorldDescriptorWire`, `IWorldCatalog`, `WorldSelector` and `ServerStartOptions`, plus world-bound constructors. The C0 agent died from a network error just before committing; the coordinator built, tested and committed its work.
3. **Implementation.** Five implementers ran in parallel (B1–B5) and merged in the order B1, B4, B2, B3, B5, all cleanly (`818e9b0`).
4. **Glue (`3105066`).** It removed the scaffolding: B2's legacy fallback and B4's test DDL. It unskipped 7 tests and fixed 2 test fixtures. It also added a test that a client receiving an unsupported world ends its session.
5. **Verification.**
   - The reviewer found 0 Must fix items and 5 Should fix items.
   - The tester found two problems:
     - The World10k full checkpoint was 18% slower than C0 (outside the 10% gate). A bisect pinned it on B1.
     - Two servers starting at the same time on an unmigrated database crashed one of them.
6. **Fix loop 1.**
   - G1: checkpoint performance.
   - G2: robustness.
   - One conflict between them: G2's race test pinned schema v3, and G1 shipped 0004. The test now follows the latest shipped version (`5deea30`).

## Changes

| Workstream | Change |
| --- | --- |
| B1 Schema | Migration 0003 adds a `worlds` table (AUTOINCREMENT). World 1 is migrated from `world_metadata`'s seed and time. Seven per-world tables are rebuilt with `world_id` first in a composite key, an FK to `worlds`, and INTEGER cells: player world state, placed cubes, rubble, enemies, items, inventory slots and starter grants. The migrator turns `foreign_keys` OFF outside the transaction, runs `foreign_key_check` before commit, and restores the setting. Repositories, checkpoint partials and the vitals guard are scoped by `$worldId`. The tables keep rowid storage, because `WITHOUT ROWID` measured slower. |
| B2 Selection | The server picks its world from `--world` or `DREAM_WORLD` through `IWorldCatalog.Resolve`, and repositories are bound to that world. The runtime map is built from the `WorldDescriptor`; the default world reproduces `CreateMap(56)` exactly. Join-accepted carries the descriptor. The seed never appears in logs. Exit code 4 means the world was not found. |
| B3 Protocol v7 | The client applies the world descriptor before `InitialWorld`. A world of a different size rebuilds the map. An unsupported generator, size or source ends the session with a reason. The HUD floor size is now only the size of a new local world. |
| B4 Catalog and CLI | `SqliteWorldCatalog` implements create, list, copy (all 7 tables, same ids, seed and time) and delete, each in one IMMEDIATE transaction. Command line: `--worlds list\|create\|copy\|delete`. Exit codes 0, 1, 2 and 3; the seed is never printed. |
| B5 Safety net | Tests for multi-world isolation, restart restore, size mismatch, starting from a v2 database with data, and lockstep across world copies. A baseline file and docs. The flaky allocation test is now measured over two warmed-up days. |
| Fix G1 (performance) | Root cause: the composite key needs two B-tree lookups per checkpoint `UPDATE`, and each `UPDATE` also rewrote an `updated_utc` index that nothing reads. Migration 0004 drops the three unused `updated_utc` indexes on cubes, rubble and enemies. The checkpoint `UPDATE`s also bind world id and write time as literals once per command, instead of as per-row parameters. |
| Fix G2 (robustness) | The migrator takes the write lock (`BEGIN IMMEDIATE`) and re-reads the version before migrating, so a second process waits and then does nothing. The backup is taken under that lock through a read-only connection. A corrupt `world_time_ticks` throws instead of reading 0. All-digit names and names with control characters are rejected. A world that cannot be opened exits with code 5. Creating a world logs a typo warning. Restore logs a count of skipped cubes. A copy shares the source's seed. Nits fixed. |

## Measured Impact

Release, `DREAM_LOAD_TEST=1`, `IncrementalCheckpointMeasurementTests`, three alternating rounds against C0 on the same machine:

| Measurement | C0 mean | Wave B before fix | After fix | After fix vs C0 |
| --- | --- | --- | --- | --- |
| World10k full checkpoint write | 42.5 ms | 49.8 ms (+18%) | 36.2 ms | −15% |
| World10k incremental write | 1.29 ms | 1.46 ms | 0.90 ms | −30% |
| Smoke8 full write | 9.66 ms | noisy | 7.88 ms | −18% |
| Smoke8 incremental write | 1.07 ms | 0.96 ms | 0.88 ms | −18% |
| Join tick (outliers excluded) | 1.27 ms | 1.26 ms | not re-measured | |
| Join-accepted message | 3,374 B | 3,509 B (+135 B, the descriptor) | | |
| Snapshot bytes over 200 ticks | 58,107 B | 58,107 B | | unchanged |

## Verification

- The full suite on `5deea30`: 3,305 passed, 25 skipped, 0 failed. This was the second of two runs; the first exposed the version conflict between G1 and G2.
- The tester covered:
  - Real legacy databases at v0, v1 and v2, copied from this machine's build output. They migrate with a backup. `integrity_check` returns ok and `foreign_key_check` is clean. The seed, time, clock phase, rows, lamp, torch and grants are all preserved.
  - The CLI end to end, in-process and as a real `dream.dll` process.
  - `--server --world foo` at size 20, with a FloorSize-56 client.
  - Two hosts on different worlds sharing one database file. They ran 600 ticks concurrently, including a held lock past the busy timeout, and lost no writes.
  - v6 clients are refused.
- Perf harness, run from a scratch copy:
  - A v1 to v3 migration on start.
  - Both v7 clients connected at 144 FPS, 26 draws, 0 GL errors.
  - All processes cleaned up.
- Not verified: interactive play.

## Known Limits And Follow-ups

- **The restore-skip summary line is hard to see.** It goes through `NetworkDiagnosticsLog`, which prints only with `DREAM_NET_DIAGNOSTICS`. The bridge needs a real log callback.
- **`ArgumentException` during server startup is caught broadly** and becomes exit code 5. The message is logged.
- **No lock between the CLI and a running server on the same world.** Documented: stop the server before running copy or delete.
- **A typo in `--world` creates a new empty world** (by design). A loud log line now points at the delete command.
- **Next on the persistence roadmap: wave C, stable object ids.**
  - A 64-bit `ObjectId` from a per-world counter, separate from the wire actor id.
  - Rubble pieces get their own ids, and enemies stop being reassigned.
  - Buildings outlive their builder (`ON DELETE SET NULL`).
