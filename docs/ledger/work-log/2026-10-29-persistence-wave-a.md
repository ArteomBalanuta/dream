# Persistence Wave A: SQL Foundation

Purpose: this log records persistence wave A of `docs/ledger/plans/2026-10-28-world-persistence-roadmap.md` (Phase 1) and its fix loop, which it shared with day/night wave T2.

Status: integrated on `integrate/engine-iteration` and verified by automated tests and Release measurements. Not pushed. Not merged into `master`.

## Process

1. **Planning.** The architect wrote `docs/ledger/plans/2026-10-28-wave-a-plan.md`. The user agreed to the recommended Phase 0 decisions:
   - many worlds in one database;
   - generated terrain plus overrides;
   - buildings outlive their builder;
   - an int on the wire;
   - existing data migrates in place as world 1.

   Wave A itself changes no schema semantics.
2. **Contracts (C0, `b145a9f`).**
   - The store (`IWorldStore`, `SqliteSession`, bind and read helpers, `SqliteRepositoryBase`), fully implemented.
   - The migration interfaces.
   - Six per-aggregate repository interfaces, behind the existing `IWorldPersistenceRepository` facade.
   - Skeleton repositories.
3. **Implementation.** Five implementers ran in parallel with wave T2: A1 to A5. A5 merged first. All merges were clean.
4. **Verification.**
   - A tester checked wave A and T2 together.
   - The reviewer found 2 Must fix items: a leave-save ordering race, and docs that described the planned guard rather than the one built.
5. **Fix loop 1.**
   - F1 handled persistence and sessions.
   - F3 handled starter grants and migration 0002.
   - The first full run after merging showed 4 test conflicts between F1 and F3. F1's tests assumed v1 was the latest version; they were fixed in `1a92684`.

## Changes

| Workstream | Change |
| --- | --- |
| A1 Migrations | `config/migrations/NNNN_*.sql` is now the source of truth, tracked in a `schema_version` table. All pending migrations run in one transaction. Before migrating a database that already has tables, it is backed up to `{db}.vFROM-to-vTO.{utc}.bak`. A database newer than the build is refused with `SchemaTooNewException` before the WAL pragma, so the file stays untouched. `0001_baseline.sql` reproduces the pre-migration schema exactly, which snapshot tests prove. `EnsureColumnExists` is gone. |
| A2 Store | The profile, combat-formula and metadata repositories run on `SqliteRepositoryBase`, which uses static state-passing lambdas. Group commit still enlists, with one commit per group. |
| A3 Repository split | The world repository is now six aggregate repositories: player world state, placed objects, rubble, enemies, items and inventory. `SqliteWorldPersistenceRepository` is a facade of 20 one-line forwards. The SQL text did not change. |
| A4 Checkpoint guard | The checkpoint writer uses an owned `BEGIN IMMEDIATE` transaction and holds no SQL of its own. `player_world_state` carries its capture time and is only overwritten by a newer one. Vitals are skipped when the player's world-state row is newer than the capture, meaning the player left after the capture was taken. That anchor is used instead of the profile's own timestamp so that lockstep stays deterministic. |
| A5 Safety net | Round-trip characterization tests. `Microsoft.Data.Sqlite` is forbidden in Simulation. Docs. Baseline measurements. |
| Fix (F1) | The leave saves the world state before the profile, which closes the race where a checkpoint committed between the two writes. `SavePlayerWorldState` stores the state's own timestamp. A missing `migrations/` folder fails startup; there is a test-only opt-in. A migration that ends its own transaction is caught. A dedicated server on a database newer than the build exits with code 3 instead of crashing. Profile saves are skipped when the interaction mode is unchanged, which a starved seat used to trigger 64 times a second. The guard docs were rewritten. |
| Fix (F3) | Migration 0002 adds `player_starter_grants` (see the T2 log). |

## Measured Impact

Release measurements with `DREAM_LOAD_TEST=1`, compared with the baseline taken at C0:

| Measurement | Baseline | +10% limit | After wave A |
| --- | --- | --- | --- |
| World10k full checkpoint write (14,001 rows) | 65.1 ms | 71.6 | 45.3 ms |
| World10k incremental write (about 100 rows) | 1.87 ms | 2.05 | 1.27 ms |
| Smoke8 full write (2,401 rows) | 11.6 ms | 12.7 | 11.7 ms (5 runs) |
| Smoke8 incremental write (97 rows) | 1.27 ms | 1.40 | 1.01 ms |
| Join tick mean (4 joins, 150-write backlog) | 1.9 ms | 2.1 | 1.61 ms |
| Write burst of 1,000 writes | 32.0 ms | 35.2 | 31.0 ms |

The tester measured these on `79c4560`, before the fix loop. The fix loop did not touch the checkpoint write path.

## Verification

- Full suite on `1a92684`: 3,180 passed, 25 skipped, 0 failed.
- The tester's loopback runs, all checked with `PRAGMA integrity_check`:
  - A database built with the frozen `init.sql` and real data migrates from v0 to v2 with a backup, then a lamp is placed and saved.
  - A v99 database is refused, and its bytes are unchanged.
  - The leave race was reproduced in the real system with a held `BEGIN IMMEDIATE` lock, and the leave's values survived.
- F1 added a test that writes a stale checkpoint between the leave's two writes. It fails when the save order is reverted.

## Known Limits And Follow-ups

- **Wall-clock steps backwards.** A row stamped in the future makes checkpoints skip that player until real time catches up. The next leave repairs it. This is documented.
- **Profile timestamps.** Profile `Save` still stamps the worker time. The guard no longer reads it.
- **No startup lock.** Two processes migrating the same v0 database at once leave the second to fail on the `schema_version` primary key, with a rollback and no friendly message.
- **Publish output.** No test checks the `migrations/` folder in `dotnet publish` output. Build output is tested.
- **Next on the persistence roadmap: wave B, worlds as rows.**
  - a `worlds` table and `world_id` on every world table;
  - a world descriptor that replaces the client's floor-size setting;
  - a protocol bump;
  - a world catalog API.
