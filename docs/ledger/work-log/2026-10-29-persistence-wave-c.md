# Persistence Wave C: Stable Object Ids

Purpose: record persistence wave C (Phase 3 of `docs/ledger/plans/2026-10-28-world-persistence-roadmap.md`) and its fix loop.

Status: integrated on `integrate/engine-iteration`. Verified by automated tests, Release measurements, real legacy databases and the perf harness. Not pushed. Not merged into `master`. Interactive play has not been checked.

## Process

1. **Plan.** The architect wrote `docs/ledger/plans/2026-10-29-wave-c-plan.md`. Its main decisions:
   - Ids are allocated on the tick thread.
   - The per-world counter is raised by `AFTER INSERT` triggers, atomically with each insert.
   - The int actor id is still the only identity on the wire, in checksums and in RNG seeds, so the protocol stays at v7.
   - Rubble piece ids exist only on the server.
   - Restore hands out compact, fresh actor ids. This is a deliberate change.
   - Items share the allocator.
   - Owners become nullable (`ON DELETE SET NULL`).
2. **C0 (`fbec55f`).** Shared contracts: `ObjectId`, `ObjectIdAllocator`, `ObjectIdentityMap`, retyped records and interfaces, and shims that kept today's behaviour. About 30 test files needed mechanical fixes.
3. **Implementation.** Four implementers ran in parallel: A (schema, SQL, counter), B (runtime identity), C (items) and D (safety net, docs, baseline). They merged in the order A, B, C, D. Four test files conflicted where A's column renames met B's changes to fresh actor ids; each was resolved by hand by combining both sides.
4. **Glue (`6250108`).** It removed the shims and their APIs: `ReassignEnemyId`, `GetMaxItemInstanceId`, the rubble `None` fallback, `AsInt`, and `RestorePersistedInventoryState`. It switched on D's five tests and added counter and trigger checks on real legacy databases. It found no production bugs.
5. **Verification.**
   - Reviewer: 0 Must fix, 3 Should fix, 3 nits.
   - Tester: passed, with no bugs found. It added 11 tests.
6. **Fix loop 1** (one implementer) covered all the review items.

## Changes

| Workstream | Change |
| --- | --- |
| A Schema | Migration 0005:<br>• `worlds.next_object_id`.<br>• A deterministic backfill (cubes, rubble groups, pieces, enemies; base above the item ids).<br>• Cubes, rubble and enemies keyed by `object_id`, rubble with `origin_object_id` and a unique `(origin, piece_index)` index.<br>• Nullable owners with `SET NULL`.<br>• Four triggers that raise the counter.<br><br>`LoadNextObjectId` returns the stored counter through a heal guard that logs `object-id-counter-healed`. Catalog Copy carries the counter across. |
| B Runtime identity | Restore registers cubes and enemies in `ObjectId` order with fresh actor ids and binds them in `ObjectIdentityMap`. Each fracture piece gets its own id, and a one-slot `_lastRetiredCube` gives the fracture its origin. A respawn is a new object. An ownerless cube is still checkpointed. The enemy reserve and reassign code is deleted. |
| C Items | Item ids come from the shared allocator, seeded from the counter. Without persistence, ids still start at 1. |
| D Safety net | Tests for crash safety, buildings outliving their builder, two lockstep servers across a restart, and ids surviving restarts. A baseline and docs. |
| Fix loop | Removed the registry's dead removed-cube set. The backup checkpoint writer now matches rubble pieces by `ObjectId`; before, it could skip a piece after restore compaction. An item id ≤ 0 is loaded but not reserved, and is logged, instead of failing the join. Cube tombstones are only recorded once checkpoint tracking is on, and are cleared on every capture. Docs and comments updated. |

## Measured Impact

Release with `DREAM_LOAD_TEST=1`.

Glue measured each workload against C0 (`fbec55f`) in alternating back-to-back rounds:

| Measurement | C0 | Merged | Ratio |
| --- | --- | --- | --- |
| World10k full checkpoint write | 32.9 ms | 31.8 ms | 0.97 |
| World10k incremental write | 0.93 ms | 0.92 ms | 0.99 |
| Write burst, 1,000 writes (median of 5) | 24.3 ms | 25.4 ms | 1.045 (the insert triggers) |
| Join tick | 6.4–6.6 ms | 6.4–6.6 ms | 1.00 |

The tester's own three runs on the merged build:

| Measurement | Mean | Baseline |
| --- | --- | --- |
| World10k full | 32.3 ms | 34.0 ms |
| World10k incremental | 0.91 ms | 0.99 ms |
| Write burst | 25.0 ms | 24.1 ms |
| Join tick, outlier excluded | 1.24 ms | 1.26 ms |
| Smoke8 full | 10.3 ms | 9.6 ms |

Smoke8 full is noisy: 7.3–12.4 ms across runs.

Snapshot bytes, checksums and the tick-equivalence goldens are unchanged. Nothing was re-pinned.

## Verification

- Full suite on `ff5c9fb`: 3,361 passed, 25 skipped, 0 failed.
- The tester checked:
  - **Adversarial crashes.** Lost newest rows, with and without a counter rollback. Counters corrupted to 1, 0, -7 and a mid-range value, each healed and logged. A kill while 15 inserts were pending. In every case ids stayed unique across kinds and below the counter.
  - **Ownerless buildings over loopback.** A's profile was deleted. B saw A's cube and rubble, damaged the cube, and the damage persisted across restarts.
  - **Lockstep across two restarts.** Checksums and database dumps were equal every tick, including `object_id` and `next_object_id`.
- Real legacy databases at v0, v1 and v2 migrate to v5 with `integrity_check` ok, an empty `foreign_key_check`, the counter above every id, and the 4 triggers present.
- Perf harness: its database migrated from v1 to v5 with a backup. 144 FPS, 31–34 draws, 0 GL errors, all processes cleaned up. That database is the integration worktree's build output, not the main checkout.
- Not verified: interactive play.

## Known Limits And Follow-ups

- **Orphan rubble rows (plan risk 6).** A cube placed into a cell that still holds rubble, then broken, writes a new rubble group without deleting the old one. With this seed that is 5 orphan rows per cycle; ids stay unique and integrity holds. Wave D should delete the replaced group.
- **`RepairCubeRecordFromPersistence` matches by cell, using the cell cached at the last capture.** If two cubes swap cells between captures and one is hit, the hit cube can take the other's identity. This predates wave C; it belongs in wave D.
- **Restored actor ids changed (deliberate).** Rolls seeded by actor id therefore differ after a restart from wave B. No golden pins this.
- **Next on the persistence roadmap: waves D and E, terrain as data.**
  - an `IMapSource` with a golden `GeneratedMapSource`;
  - terrain chunks and per-cell overrides;
  - persisted terrain edits;
  - stored worlds sent to clients;
  - an `IMapGenerator` plug point.
