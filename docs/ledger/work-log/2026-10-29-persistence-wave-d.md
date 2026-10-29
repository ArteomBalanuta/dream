# Persistence Wave D: Terrain As Data

Purpose: record persistence wave D (Phase 4 of `docs/ledger/plans/2026-10-28-world-persistence-roadmap.md`) and its fix loop.

Status: integrated on `integrate/engine-iteration`. Verified by automated tests, Release measurements, real legacy databases and the perf harness. Not pushed. Not merged into `master`. Interactive play has not been checked.

## Process

1. **Plan.** The architect wrote `docs/ledger/plans/2026-10-29-wave-d-plan.md`. Its main decisions, with the user decisions applied at their recommended defaults:
   - Terrain comes from an `IMapSource`; the generated source must reproduce today's map exactly (pinned by goldens).
   - Destroyed terrain stays destroyed across a restart and its rubble is persisted (U1). Damaged terrain that is still standing heals on restart (U2).
   - Removals are stored as per-cell overrides that share the world's object-id counter; a stored world is a set of chunk blobs (format v1, "DTCK").
   - Orphan rubble is pruned on the first restart (U5). No backfill (U6). Reset-terrain is wave E (U3).
   - Stored worlds can live in the database but cannot be hosted until wave E.
2. **C0 (`de5e443`, `06638cc`, `99ff5f4`, merged as `ca5d7d3`).** Startup map goldens, the map-source seam (`MapDefinition`, `IMapSource`, `GeneratedMapSource`, `Map.IsSourceCell`/`RemoveSourceCell`), persistence contracts and the combat hook. Building the seam exposed a real gameplay bug: a destroyed sculpture cell left a ghost collider in the merged static collision. Fixed in `99ff5f4`.
3. **Disk full.** The first launch of the four workstreams stopped when the G: drive filled up. With the user's approval the build output of 195 finished agent worktrees was deleted (about 230 GB), and the workstreams were relaunched.
4. **Implementation.** Four implementers ran in parallel: A (schema and SQL), B (runtime terrain edits), C (chunk codec, stored source, exporter) and D (safety net, measurements, docs). They merged in the order A, C, B, D with no textual conflicts.
5. **Glue (`3e8460d`).** It unskipped D's tests and fixed D's guessed API names. It also added real-SQLite tests for terrain edits, and added the terrain tables to the per-world test helpers. Real legacy databases migrated to v6. One test failed only on some runs, because its assertion predated the orphan-rubble fix; the assertion now allows a piece to vanish only when newer rubble replaced it.
6. **Verification.**
   - Reviewer: 0 Must fix, 4 Should fix, 6 nits.
   - Tester (`d26ca15`): no production bugs. It added 5 adversarial tests and ran Release measurements and the perf harness.
7. **Fix loop 1 (`18b343a`)** covered all review items and nits.
8. **Re-review** of loop 1: 0 Must fix. Its report named three codec tests that were not in the tree, and the heal had no negative tests.
9. **Fix loop 2 (`fa73447`)** wrote those tests for real, and confirmed they are in the tree. It also:
   - adds heal tests: positive with the log line, plus three negative cases;
   - never heals a cell that already has an override row;
   - runs the heal before placed cubes are restored, so a player cube in a healed cell returns on the same restart.

## Changes

| Workstream | Change |
| --- | --- |
| A Schema | Migration 0006:<br>• `terrain_cell_overrides`, with a unique cell index and an `AFTER INSERT` trigger that raises the counter (5 counter triggers in total).<br>• `terrain_chunks` (`WITHOUT ROWID`).<br>• Cascades on world delete.<br><br>`SqliteTerrainOverrideRepository`: a duplicate cell is ignored and leaves the counter alone. `SqliteTerrainChunkRepository`: `ReplaceTerrainChunks` is atomic in its own transaction. The heal guard covers overrides. World copy carries overrides (same ids) and chunks. `WorldSeeder.SeedTerrainOverrides`. |
| B Runtime edits | A destroyed source cell writes one override and takes an id from the counter. Its rubble uses the override id as origin, with no owner. Restore applies overrides before cubes and reserves their ids; rows that can't be applied are logged and kept. Fracturing a cell that already has rubble deletes the old group (fixes the wave C orphan rows), and incremental checkpoints carry the tombstone. On restart, extra groups in one cell are pruned and logged. |
| C Codec | `TerrainChunkCodec` writes DTCK v1 blobs. The decoder accepts only canonical blobs and length-checks every read. `StoredMapSource` checks full chunk coverage and decodes fresh records in canonical order. `TerrainChunkExporter.Export(IMapSource)` and `Export(Map)`: the latter also exports the y == 0 floor cells and empty edge chunks, which deviates from the plan text. |
| D Safety net | Loopback test: destroy terrain and a sculpture cell, restart, and a joining client matches the server. Lockstep test: two servers with terrain fractures and two restarts. Stored-terrain round trip through SQLite. Terrain measurements. Docs in `docs/server-persistence-ddl.md` and `docs/architecture.md`. The C0 baseline. |
| Fix loop | **Pruning order:** when one cell has several rubble groups, keep the one with the newest pieces, not the highest origin. A cube moved by physics can have an older origin but newer rubble. **Lost override row:** a kept rubble group whose cell is still standing is healed on restore (the cell is removed, the row is rewritten with the origin id, and `terrain-override-healed` is logged). **Codec:** `Encode` rejects more than 65,535 cells per chunk; `Decode` validates chunk coordinates and the floor flag. **Early returns:** a fracture whose rubble can't be persisted still deletes the cell's old group. Doc and comment fixes.<br><br>Loop 2:<br>• Restore order: overrides, then cube rows registered, then rubble groups pruned and healed, then cubes placed, then rubble restored.<br>• A cell with an existing override row is never healed. |

## Measured Impact

Release with `DREAM_LOAD_TEST=1`, two alternating back-to-back rounds, tip `3e8460d` against C0 (`ca5d7d3`):

| Measurement | C0 | Wave D | Ratio |
| --- | --- | --- | --- |
| World10k full checkpoint write | 33.8 ms | 32.3 ms | 0.96 |
| World10k incremental write | 0.95 ms | 0.95 ms | 1.00 |
| Write burst, 1,000 writes | 24.9 ms | 26.1 ms | 1.05 |
| Join tick, mean | 6.7 ms | 6.5 ms | 0.97 |
| World10k join 0 transfer | 232,677 B | 232,677 B | identical |

Stored World10k map (100×8×100):

| Measurement | Value |
| --- | --- |
| Chunks | 49 |
| Blob bytes | 215,996 B (gate 256 KB); about 288 KB as base64, which is wave E's transfer budget |
| Encode | 40.4 ms |
| `ReplaceTerrainChunks` / `LoadTerrainChunks` | 1.8 ms / 1.5 ms |
| Load + build vs generated build, same process | 45.6 ms vs 59.5 ms (0.77×, gate 1.5×) |

Seeded overrides: the generated World10k map has only 628 raised terrain cells, so the plan's 1,000 overrides cannot be seeded. With all 628 removed: host creation 428.5 ms, join 108 ms, 4,385 B sent, and the client checksum matches the server.

Snapshot bytes, checksums and the startup goldens are unchanged. Nothing was re-pinned.

## Verification

- Full suite on `fa73447`: 3,497 passed, 28 skipped (load tests only), 0 failed. On `18b343a`: 3,489 passed.
- The tester checked:
  - **Lost override row with surviving rubble.** Before the fix loop the terrain came back with rubble inside it. It now heals on restore, and ids stay unique.
  - **Corrupt chunk blobs.** Five corruptions of a stored blob each fail loudly with `InvalidDataException`.
  - **Destroying the same cell twice**, in one session and across a restart, keeps one override row.
  - **World copy isolation.** Destroying terrain in a copy leaves the source world unchanged.
  - **Override ids** are below the counter, unique and disjoint from every other id.
- Real legacy databases at v0 (three) and v5 (one) migrate to v6 with `integrity_check` ok, an empty `foreign_key_check`, 5 counter triggers, and the counter above every id. A host started on each copy restored its rubble with no invariant violations.
- Perf harness: v5 to v6 migration with a backup. Both clients at 144 FPS, 31 and 34 draws, 0 GL errors. All processes cleaned up.
- Cross-process differences in the scripted session come from each fresh database's random world seed feeding combat damage rolls, not from a wall-clock input. In-process lockstep tests share a database and stay equal.
- Not verified: interactive play.

## Known Limits And Follow-ups

- **Stored worlds cannot be hosted yet.** That is wave E:
  - send stored terrain to clients (protocol v8, about 288 KB for World10k);
  - an export CLI;
  - edits on stored worlds;
  - a soak test;
  - reset-terrain (U3).
- **`Replaced` overrides are logged and skipped.** Only removals are supported.
- **Damaged but standing terrain heals on restart (U2).** Persisting terrain health is optional wave E work.
- **Cross-process test reproducibility.** Tests that create fresh databases get a random seed. Tests that compare separate processes should create the world with a fixed seed.
- **`RepairCubeRecordFromPersistence` still matches by cell** (wave C limitation), and is not addressed here.
