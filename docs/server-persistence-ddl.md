## Purpose

Document the authoritative SQLite schema that backs player profiles, world state, enemies, placed cubes, items, and inventory.

## Source Of Truth

The ordered migration scripts in [dream/config/migrations](/G:/workspace/projects/dream/dream/config/migrations) are the source of truth for the schema (persistence wave A, plan `docs/ledger/plans/2026-10-28-wave-a-plan.md`). `0001_baseline.sql` is the schema every existing database already has: the statements of `player-profiles.init.sql` byte for byte, plus the `world_metadata` table. Every later change is a new `NNNN_snake_case.sql` file. Persistence wave B (plan `docs/ledger/plans/2026-10-29-wave-b-plan.md`) added `0003_worlds.sql`: worlds as rows (see Worlds below). Persistence wave C added `0005_object_ids.sql`: stable object ids (see Stable object ids below). Persistence wave D added `0006_terrain.sql`: terrain overrides and exported terrain chunks (see Terrain as data below).

[dream/config/player-profiles.init.sql](/G:/workspace/projects/dream/dream/config/player-profiles.init.sql) is frozen. It is the pre-migration schema. It stays as the legacy fixture that tests build old databases from, and because `SqliteSchemaBootstrapper` still takes its path: the migrations directory is found next to it (`Path.Combine(Path.GetDirectoryName(schemaScriptPath), "migrations")`). Do not edit it to change the schema; add a migration.

This document explains the intent behind each table so future runtime and migration work stays aligned with the current denormalized design.

## Schema Versions And Migrations

`SqliteSchemaBootstrapper.EnsureSchema` runs on every startup. It sets `PRAGMA journal_mode=WAL` (outside any transaction), then hands the connection to `SqliteSchemaMigrator` (`dream/Server/Persistence/Migrations/`), which applies the pending migrations.

**Files.** A migration is `dream/config/migrations/NNNN_snake_case.sql`, matching `^(\d{4})_([a-z0-9_]+)\.sql$`. The number is the version: versions start at 1 and are contiguous, and a gap or a duplicate fails startup. The name is the rest of the file name. The existing `config\**\*` copy glob ships the directory with the build, and a missing `migrations` directory fails startup (`DirectoryNotFoundException`) before the database is opened. Only tests can opt into treating a lone schema script as its own baseline (the internal `SqliteSchemaBootstrapper` constructor flag). Scripts contain no `BEGIN`, `COMMIT`, `ROLLBACK`, `END TRANSACTION` or `PRAGMA journal_mode`: the migrator owns the transaction and the bootstrapper owns the journal mode. Loading rejects a script with any of these at the start of a line. A statement later on a line (`...; COMMIT;`) gets past that check, so after each migration the migrator also checks that its transaction is still open (`sqlite3_get_autocommit`) and otherwise fails startup with "migration NNNN ended the transaction". What that script ran before the `COMMIT` is already committed then; restore the backup.

**`schema_version`.** One row per applied migration:

```sql
CREATE TABLE IF NOT EXISTS schema_version (
    version INTEGER NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    applied_utc TEXT NOT NULL
);
```

The current version is the highest `version`; a database without the table is at version 0. A database from before wave A is therefore at 0 and gets `0001_baseline` on its first start.

**One transaction.** All pending migrations run in one transaction, together with their `schema_version` rows. If any of them fails, all of them roll back and the database stays at its old version.

**Backup first.** When migrations are pending and the database already holds user tables (it is not brand new), the migrator copies the file with `SqliteConnection.BackupDatabase` before it applies anything. The copy sits next to the database as `{db}.v{from:D4}-to-v{to:D4}.{yyyyMMddTHHmmssZ}.bak`, for example `world.sqlite.v0000-to-v0001.20261028T120000Z.bak`. A fresh database gets no backup, and neither does a start with nothing pending. The bootstrapper switches the database to WAL before the migrator runs, so the backup's file header records WAL mode even when the original was a rollback-journal (pre-wave-A) file; its data is the pre-migration data.

**Too new.** A stored version above the newest migration the build knows means a newer build wrote the database. Startup throws `SchemaTooNewException` and leaves the file untouched. The dedicated server (`--server`) catches it, logs which versions clash and exits with code 3 (`ServerBootstrap.SchemaTooNewExitCode`) instead of crashing.

**The baseline.** `0001_baseline` (`BaselineMigration`) runs the idempotent baseline script (`IF NOT EXISTS` everywhere, `ON CONFLICT DO NOTHING` for the formula seed), then adds the four columns that older files may lack: `player_profiles.experience` and `placed_cube_instances.center_x`, `center_y` and `center_z`. It is the only migration that probes columns, and it runs only from version 0 to 1. Later migrations assume the schema of the version before them.

**Behaviour change: the formula seed runs once.** Before wave A the whole init script ran on every startup, so a deleted `combat_formulas` seed row came back on the next start. The baseline now runs once, so a deleted seed row stays deleted. Edited formulas were already kept (`ON CONFLICT DO NOTHING`), and still are. To restore a deleted default, insert it again, or add a migration that does.

## Worlds (migration 0003)

Since schema version 3 one database holds any number of worlds. A world is a row of `worlds`; seven tables hold per-world state and carry `world_id` as the first column of a composite primary key.

**Per world:** `player_world_state`, `placed_cube_instances`, `cube_fracture_pieces`, `enemy_instances`, `item_instances`, `player_inventory_slots`, `player_starter_grants`.

**Global (no `world_id`):** `player_profiles` (including vitals), `player_profile_change_log`, `combat_formulas`, `schema_version`, and `world_metadata` (kept as a generic key/value table; it no longer holds the seed or the world time).

Inventory belongs to a world because items exist in it: an item instance id is only unique within its world, and a player's slots in world 2 are not the slots in world 1. The starter grant is measured against that world's inventory.

### The `worlds` table

```sql
CREATE TABLE worlds (
    world_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    width INTEGER NOT NULL, height INTEGER NOT NULL, depth INTEGER NOT NULL,
    map_source TEXT NOT NULL,          -- 'generated' | 'stored'
    generator_key TEXT NOT NULL,
    generator_version INTEGER NOT NULL,
    seed TEXT NULL,                    -- decimal unsigned 64-bit; NULL = generate on first load
    world_time_ticks INTEGER NOT NULL DEFAULT 0,
    created_schema_version INTEGER NOT NULL,
    created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    CHECK (length(trim(name)) > 0), CHECK (width >= 1 AND height >= 1 AND depth >= 1),
    CHECK (map_source IN ('generated', 'stored')), CHECK (length(trim(generator_key)) > 0),
    CHECK (generator_version >= 1), CHECK (created_schema_version >= 1)
);
CREATE UNIQUE INDEX ux_worlds_name ON worlds (name COLLATE NOCASE);
```

- `world_id` is `AUTOINCREMENT`, so a deleted world's id is never reused.
- Names are unique ignoring case. The catalog trims them and allows 1 to 64 characters.
- Allowed sizes: width equals depth, 5 to 100; height 8; generator `default` version 1; map source `generated`. `WorldDescriptor.Validate` enforces this when a world is created or opened.
- `seed` is the same decimal text the old `simulation_seed` row held. `SqliteWorldMetadataRepository.GetOrCreateSimulationSeed` fills a NULL seed with `UPDATE worlds SET seed = $seed WHERE world_id = $worldId AND seed IS NULL` and re-reads it, so two racing loads keep the first. A seed that is not a decimal unsigned 64-bit number is a startup error (`InvalidDataException`).
- `world_time_ticks` is the world clock (`WorldClock.WorldTicks`). `SqlitePersistenceCheckpointWriter` writes it with `UPDATE worlds SET world_time_ticks = $ticks WHERE world_id = $worldId` inside each checkpoint transaction whose `PersistenceCheckpoint.WorldTimeTicks` is set. A negative or non-integer value is a startup error.
- The seed and the time are server-only. The wire carries only the public part of the row (`WorldDescriptor`, protocol v7), never the seed. Tick length per day is not stored: `DREAM_DAY_LENGTH_MINUTES` keeps control.
- Migration 0003 inserts world 1, named `default`, 56x8x56, generated, generator `default` version 1, with `created_schema_version` 3. Its seed and time come from the old `world_metadata` rows, which the migration then deletes.

### Keys, storage and types

| Table | Primary key | Storage |
| --- | --- | --- |
| `player_world_state` | `(world_id, player_name)` | rowid table |
| `placed_cube_instances` | `(world_id, cube_id)` | rowid table |
| `cube_fracture_pieces` | `(world_id, source_cube_id, piece_index)` | rowid table |
| `enemy_instances` | `(world_id, enemy_id)` | rowid table |
| `item_instances` | `(world_id, item_instance_id)` | rowid table |
| `player_inventory_slots` | `(world_id, player_name, slot_index)` | rowid table |
| `player_starter_grants` | `(world_id, player_name)` | rowid table |

- Every per-world table has `FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE`. Other foreign keys are as before (owners to `player_profiles`: `CASCADE`, except `item_instances.owner_player_name`, which stays `SET NULL`). `player_inventory_slots` references items by `(world_id, item_instance_id)`.
- All seven tables are rowid tables with a composite primary key (an index on the key columns), including the three busy ones (cubes, rubble, enemies). `WITHOUT ROWID` was tried for those three and measured slower: on World10k a full checkpoint was about 10% slower than the baseline with it, and about 4% slower with a rowid table and the composite key index. The composite key makes each checkpoint UPDATE two B-tree lookups (key index, then rowid) where the old `INTEGER PRIMARY KEY` was one, and the `updated_utc` index of those three tables is rewritten on every UPDATE; together with the `$worldId` bind this made the World10k checkpoint about 12% slower than before 0003, so migration 0004 drops the three `updated_utc` indexes (below).
- `placed_cube_instances.cell_x/y/z` and `cube_fracture_pieces.parent_cell_x/y/z` are now `INTEGER NOT NULL`. Existing `REAL` values were whole numbers and convert without loss; the code that binds and reads them does not change.
- Indexes keep their names. `ix_cube_fracture_pieces_parent_cell` is now `(world_id, parent_cell_x, parent_cell_y, parent_cell_z)` and `ix_player_inventory_slots_item_instance_id` is `(world_id, item_instance_id)`.
- `ON DELETE SET NULL` for cube owners arrived with migration 0005 (see Stable object ids below); in schema version 3 and 4 owners still cascade.
- Object ids restart per world, and item ids come from the per-world `next_object_id`; actor ids are not persisted. Inventory does not travel between worlds.

### How the code is bound to a world

One repository instance serves one world: the world id is given at construction (`new SqliteWorldPersistenceRepository(schema, worldId)`, `SqlitePersistenceCheckpointWriter(schema, worldId)`, `SqliteWorldMetadataRepository(schema, worldId)`; the constructors without a world id mean world 1) and bound as `$worldId` in every statement. One server process runs one world. The upsert checkpoint binds `$worldId` once per prepared command and its `ON CONFLICT` targets are the composite keys. The cube, rubble and enemy checkpoint UPDATEs (which run once per row) carry the world id and the write time as SQL literals instead of parameters (`SqliteRepositorySql.WithCheckpointLiterals`): Microsoft.Data.Sqlite looks each parameter up by name and binds it on every execution. The `updated_utc` guard on profile vitals gained `AND player_world_state.world_id = $worldId`.

The catalog (`SqliteWorldCatalog`, `Dream.Server.Worlds.IWorldCatalog`) lists, creates, copies, deletes and resolves worlds. Copy and delete run in one transaction each, with explicit column lists. A copy gets every world-scoped row with the same local ids, and the source's seed and world time. A delete removes the per-world tables' rows explicitly, children first, then the world row. The offline `--worlds copy`, `delete`, `export` and `reset-terrain` commands take the world's host lease (`{db}.world-{id}.lock`, see `docs/configuration.md`) and exit 7 when a server holds it, so stop the server first. A migration that is pending also needs every world's lease. The `--worlds` commands hold `{db}.maintenance.lock` from the migration until the command ends (lease acquisition and the whole of `create` included), and a start holds it through its migration, world resolve and lease acquisition, so migrations, `--db restore` and world creation never interleave across processes.

### Migration 0004: busy-table `updated_utc` indexes dropped

`0004_drop_busy_table_updated_utc_indexes.sql` drops `ix_placed_cube_instances_updated_utc`, `ix_cube_fracture_pieces_updated_utc` and `ix_enemy_instances_updated_utc`. Every checkpoint UPDATE sets `updated_utc`, so each row paid an index delete and insert for an index nothing reads (no query filters or orders by it). Isolated, the index was about 25% of a 10000-row UPDATE loop. No table is rebuilt and no row changes. The other `updated_utc` indexes (`player_profiles`, `player_world_state`, `item_instances`) stay. A database already at version 3 takes the new `v0003-to-v0004` backup like any upgrade.

### How 0003 migrates

Rows move with the SQLite table-rebuild procedure: create `X__v3`, `INSERT ... SELECT 1, <old columns>` into it, `DROP TABLE X`, `ALTER TABLE X__v3 RENAME TO X`, recreate the indexes. The old table is never renamed out of the way first, because SQLite 3.26 and later would rewrite child foreign keys to point at the renamed table.

`SqliteSchemaMigrator.Migrate` changed for this:

- **Foreign keys are off while migrations run.** `PRAGMA foreign_keys` is a no-op inside a transaction, and with foreign keys on, `DROP TABLE` performs an implicit `DELETE` that fires `ON DELETE CASCADE`: dropping `item_instances` would wipe the inventory slots. Outside the transaction the migrator reads the pragma, switches it off if it was on, and restores the original value in a `finally`, because pooled connections keep the pragma.
- **`PRAGMA foreign_key_check` after the last migration**, inside the transaction. Any row makes the migrator throw `InvalidOperationException` naming the table, rowid and parent (up to 10 rows). That rolls everything back; the version stays where it was and the backup already exists. An orphan row in a pre-0003 database therefore fails startup loudly instead of being copied.
- `legacy_alter_table` stays OFF.

A v2 database upgrading takes the usual `{db}.v0002-to-v0003.{timestamp}.bak` backup.

## Stable object ids (migration 0005)

Persistence wave C (plan `docs/ledger/plans/2026-10-29-wave-c-plan.md`) gave every persisted world object a 64-bit `ObjectId` that is never reused: placed cubes, rubble pieces, enemies and items. Schema version 5. The sections above and the per-table notes below describe the pre-0005 key names (`cube_id`, `source_cube_id`, `enemy_id`) where they say so; 0005 renames them as listed here. The int actor id stays the identity on the wire, in checksums and in RNG seeds; `ObjectId` only exists in persistence (see `docs/architecture.md`, Object ids and actor ids).

**The counter.** `worlds.next_object_id INTEGER NOT NULL DEFAULT 1` holds the next id to issue in that world (added with `ALTER TABLE`; `worlds` is not rebuilt, because 7 child foreign keys point at it). Restore seeds the runtime's single `ObjectIdAllocator` from it. All four object kinds draw from that one allocator, so an id is unique across kinds within a world.

**Keys.**

| Table | Primary key | Id columns |
| --- | --- | --- |
| `placed_cube_instances` | `(world_id, object_id)` | `object_id` replaces `cube_id` |
| `cube_fracture_pieces` | `(world_id, object_id)`, plus `UNIQUE INDEX ux_cube_fracture_pieces_origin (world_id, origin_object_id, piece_index)` | `object_id` is the piece's own id; `origin_object_id` replaces `source_cube_id` and is the retired id of the cube that fractured |
| `enemy_instances` | `(world_id, object_id)` | `object_id` replaces `enemy_id` |
| `item_instances` | `(world_id, item_instance_id)` | unchanged name; the value is an ObjectId from the shared allocator |
| `player_inventory_slots` | unchanged | `item_instance_id` is unchanged |

All rebuilt tables stay rowid tables. The `updated_utc` indexes dropped by 0004 are not recreated; the owner and `parent_cell` indexes are.

**Nullable owners.** `owner_player_name` is nullable on `placed_cube_instances` and `cube_fracture_pieces`, with `CHECK (owner_player_name IS NULL OR length(trim(owner_player_name)) > 0)` and `ON DELETE SET NULL` instead of `CASCADE`. Deleting a player profile therefore leaves the player's buildings and rubble in the world with no owner (their inventory slots still cascade away). A new placement still needs a valid owner to get an `ObjectId`; an ownerless cube that already has an id is still checkpointed and can still be damaged.

**Triggers: any rebuild must recreate them.** Four `AFTER INSERT` triggers keep the counter above every inserted id, in the same statement as the insert, so a crash cannot leave a persisted id at or above the stored counter:

- `trg_placed_cube_instances_advance_object_id` (on `object_id`)
- `trg_cube_fracture_pieces_advance_object_id` (on `object_id`)
- `trg_enemy_instances_advance_object_id` (on `object_id`)
- `trg_item_instances_advance_object_id` (on `item_instance_id`)

Each runs `UPDATE worlds SET next_object_id = NEW.<id> + 1 WHERE world_id = NEW.world_id AND next_object_id <= NEW.<id>`: it raises the counter and never lowers it, and an `ON CONFLICT DO UPDATE` or a checkpoint `UPDATE` does not fire it (neither is an insert). `DROP TABLE` drops a table's triggers, so a later migration that rebuilds one of these four tables must recreate its trigger (a migration test checks this). An id reissued after a crash belonged to an insert that never committed, so nothing references it.

**Deterministic backfill.** 0005 renumbers existing rows per world, so two copies of one v4 database migrate to identical v5 databases:

- `base = MAX(item ids in item_instances and player_inventory_slots) + 1`, or 1 when there are none. Items keep their ids.
- Ids are then assigned in this order: cubes (by `cube_id`), rubble groups (one per distinct `source_cube_id`, which become retired `origin_object_id` values), rubble pieces (by `source_cube_id, piece_index`), then enemies (by `enemy_id`).
- `next_object_id = base + the number of assigned ids`.

A cube and an enemy that shared a legacy integer id get different ObjectIds. The migration builds the mapping in a scratch table (`object_id_backfill__v5`) and drops it. It uses the 0003 rebuild pattern (create `X__v5`, copy, drop, rename, recreate indexes), under the migrator's existing foreign-keys-off and `foreign_key_check` handling, so an orphan row fails the migration and the version stays at 4. A v4 database takes the usual `v0004-to-v0005` backup.

**Heal guard.** `LoadNextObjectId()` (`IObjectIdCounterRepository`) returns `max(stored next_object_id, MAX(id) + 1)` over the four object tables and the inventory slots. When the stored value is lower (a hand-edited or restored file), it raises `worlds.next_object_id` and logs `server.persistence.object-id-counter-healed`. The bridge also reserves every id it loads, so the allocator cannot hand out a loaded id.

**Other behaviour.**

- Catalog copy copies the new columns and `next_object_id` with them.
- Load order is `ORDER BY object_id` for cubes and enemies and `ORDER BY origin_object_id, piece_index` for rubble. Restore registers cubes and enemies in that order, so their compact actor ids follow it.
- Orphan rubble rows (pieces whose group is no longer restorable) were never deleted in wave C. Wave D (migration 0006) deletes them: see **Orphan rubble** under "Terrain as data" below.

## Terrain as data (migration 0006)

Persistence wave D (plan `docs/ledger/plans/2026-10-29-wave-d-plan.md`) stores terrain edits and exported terrain. Schema version 6. Migration 0006 adds two empty tables and one trigger; no existing row or table changes (terrain destroyed before wave D has already come back and is not recovered). Nothing changes on the wire: the protocol stays at v7.

**`terrain_cell_overrides`: edits on a generated world.** One row per base-terrain cell that was destroyed. The cell stays destroyed after a restart; its rubble is stored as ordinary `cube_fracture_pieces` rows whose `origin_object_id` is this row's `object_id`.

| Column | Notes |
| --- | --- |
| `world_id`, `object_id` | Primary key. `object_id` comes from the world's shared `ObjectIdAllocator`, so it is unique across kinds. |
| `cell_x`, `cell_y`, `cell_z` | The terrain cell. `CHECK (cell_x >= 0 AND cell_y >= 1 AND cell_z >= 0)`: the floor (`y = 0`) is never an override. `UNIQUE INDEX ux_terrain_cell_overrides_cell (world_id, cell_x, cell_y, cell_z)`. |
| `kind` | `0 = Removed` (the only kind written in wave D), `1 = Replaced` (reserved; restore logs `terrain-override-unsupported` and skips it). |
| `cell_data` | NULL for `Removed`, NOT NULL for `Replaced` (`CHECK`). |
| `created_utc`, `updated_utc` | Stamped by default. |

A foreign key to `worlds` with `ON DELETE CASCADE`. A row is written once, when the cell is destroyed, with `INSERT ... ON CONFLICT(world_id, cell_x, cell_y, cell_z) DO NOTHING`, so a second insert for a cell is ignored and leaves the counter alone. Overrides are never checkpointed or updated. The trigger `trg_terrain_cell_overrides_advance_object_id` (the fifth object-id trigger, on `object_id`) keeps `worlds.next_object_id` above every override id, exactly like the four from 0005; a migration test counts the 5 triggers. The heal guard in `LoadNextObjectId()` also takes the maximum over `terrain_cell_overrides`. Loading is `ORDER BY object_id`.

**Restore.** After the object id allocator is seeded and before placed cubes, rubble and enemies are restored, each `Removed` override is applied with `Map.RemoveSourceCell(cell)`, the same removal path a live destruction takes, in `object_id` order. An override whose cell is not a source cell of the map (for example the world was resized) is logged as `server.persistence.terrain-override-skipped` and its row is kept. Clients are not told: they generate the base terrain and the join snapshot removes every raised cube that the server no longer has. Terrain that is damaged but still standing is not stored and heals on restart (decision U2).

**Healing a lost override.** After rubble is restored, a group whose cell is still a source cell, whose origin is neither a known override id nor a persisted cube id, means the `Removed` override row was lost (a crash between the writes, or a deleted row). Restore removes the cell with `Map.RemoveSourceCell`, so rubble never overlaps returning terrain, and re-enqueues the override row with the group origin as its `object_id` (already reserved). Groups are healed in origin order and logged as `server.persistence.terrain-override-healed count=`.

**Export(Map).** `TerrainChunkExporter.Export(Map)` includes the y == 0 floor cells and the empty edge chunks of the chunk grid. The codec rejects a chunk of more than 65,535 cells (the cell count is 16 bits) and enforces the floor rule: an `IsFloor` palette entry only at y == 0, and no non-floor cell at y == 0.

**Orphan rubble.** When several rubble groups map to one cell on restore, the group with the highest piece `object_id` wins (piece ids are taken at fracture time, so they follow fracture order; an origin is a placement id and a cube that physics moved to another cell and broke later can have a lower one) and the others are deleted and logged as `server.persistence.orphan-rubble-pruned count=` (decision U5). A group that is replaced while the cell is live (a cube placed over rubble and then broken) deletes the old group's rows on the write queue before the new group's rows; checkpoints carry the tombstone. This also happens when the new group cannot be persisted (no ObjectId or owner), so a stale group is never left behind. This supersedes the wave C note that orphan rubble rows are never deleted.

**`terrain_chunks`: exported terrain.** One row per 16 x 16 column chunk of a map, in a `WITHOUT ROWID` table: `(world_id, chunk_x, chunk_z)` primary key, `format_version`, `cell_count`, `data` (the blob below) and `updated_utc`. Checks: `chunk_x >= 0 AND chunk_z >= 0`, `format_version >= 1`, `cell_count >= 0`, `length(data) >= 23`. Foreign key to `worlds` with `ON DELETE CASCADE`. `ReplaceTerrainChunks` deletes the world's chunks and inserts the new ones in one transaction, so a failure leaves the old set; `LoadTerrainChunks` is `ORDER BY chunk_z, chunk_x`. It is an admin and export path (`ITerrainChunkRepository`), not part of the write queue or the world facade. Catalog copy copies overrides with the same ids, the chunks, and the counter. A `stored` world is hosted from wave E: see **Stored worlds, export and reset (wave E)** below.

**Chunk blob format v1** (`TerrainChunkCodec`, `Dream.Simulation.World.Terrain`). Little-endian, uncompressed, deterministic; the decoder throws `InvalidDataException` on any violation, and encode and decode are exact inverses at the level of effective cell records.

```
off 0   u8[4]  magic "DTCK"
    4   u16    formatVersion = 1
    6   u16    reserved = 0
    8   i32    chunkX            12  i32 chunkZ     (must equal the row's columns)
   16   u8     sizeX = 16        17  u8 sizeZ = 16  18  u16 height (1..1024)
   20   u16    cellCount (<= sizeX * sizeZ * height)
   22   u8     paletteCount (0 iff cellCount == 0; <= 127)
   23   palette, 4 B each: u8 shapeKind, u8 materialId, u8 subdivisionResolution,
        u8 flags (bit0 IsSolid, bit1 IsFloor). Order: first appearance in ascending cell index.
        occupancy bitmap: ceil(sizeX * sizeZ * height / 8) B, LSB-first; bit i = local cell
        i = ((lx * sizeZ) + lz) * height + y. Bits for cells outside the world must be 0.
        cell bytes, one per set bit ascending: bit7 = has subdivision state, bits0-6 = palette index.
        subdivision records, one per cell with bit7, ascending:
          u8 resolution (1..40), u8 flags (bit0 AllChildrenEmpty, bit1 has false overrides),
          set(true overrides), then set(false overrides) if bit1.
          set := u8 mode; mode 0: bitmask ceil(res^3 / 8) B, LSB-first, child c = (sx * res + sy) * res + sz;
                 mode 1: u16 count, then count x u16 ascending c. The encoder picks the shorter, tie -> mode 0.
        no trailing bytes.
```

The encoder rejects overrides outside `[0, res)`, fracture pieces and more than 127 palette entries. `TerrainChunkFormat.CurrentVersion = 1`, `ChunkSize = 16`.

## Stored worlds, export and reset (wave E)

Persistence wave E (plan `docs/ledger/plans/2026-10-29-wave-e-plan.md`) hosts worlds whose terrain is `terrain_chunks`. **No schema change**: the schema stays at version 6 and no migration is added (decision E6). Migration 0003 already allows `map_source IN ('generated','stored')`, and 0006 already has `terrain_chunks` and `terrain_cell_overrides`. A legacy database stays at v6 with no backup taken.

**What a stored world is.** A `worlds` row with `map_source = 'stored'`, the bounds of its map (square, 5 to 100, height 8, the same hosting limits as a generated world), and a complete set of `terrain_chunks` rows: exactly `ceil(W/16) x ceil(D/16)` chunks, format v1, none duplicated. On a stored world `generator_key` and `generator_version` are provenance only (they record where the terrain came from); `WorldDescriptor.Validate` requires a non-blank key and a version of at least 1 and does not look them up in the catalog. The server loads the chunks once at start and the blobs are sent to clients unchanged; the table is never written while a world is hosted.

**Edits.** `terrain_cell_overrides` rows and their rubble work exactly as on a generated world (see Terrain as data): an override removes a cell of the stored base, in `object_id` order, before cubes are restored. The stored chunks hold the base as exported; an edit never rewrites them.

**Export** (`--worlds export <src> <new name> [--terrain-only]`) is one transaction (`IWorldCatalog.CreateStored`) that inserts a new `worlds` row with `map_source = 'stored'`, the source's bounds and its provenance generator key and version (for a stored source, its own provenance), and the chunks. The chunks are `TerrainChunkExporter.Export(map)` of the source's map after its `Removed` overrides were applied (`Replaced` overrides are counted and skipped, as on restore), so destroyed cells are baked out. Export is deterministic: the same database state gives byte-identical blobs.

- Full export (without `--terrain-only`): copies every world-scoped row as `--worlds copy` does, with the same ids, counter, seed and world time, **except `terrain_cell_overrides`**, which are baked into the chunks. The rubble rows are copied, so the new world shows the same rubble where cells were destroyed.
- `--terrain-only`: the chunks only. A new seed, `world_time_ticks = 0`, `next_object_id = 1`, and no player, cube, rubble, enemy, item or override rows.

**Reset-terrain** (`--worlds reset-terrain <id|name> --yes`, `IWorldCatalog.ResetTerrain`) is one transaction. For every override cell that holds no `placed_cube_instances` row of that world: delete the override and every `cube_fracture_pieces` row whose parent cell is that cell (any origin, so the restore heal cannot remove the cell again), then every `cube_fracture_pieces` row of the world whose `origin_object_id` is neither a `terrain_cell_overrides.object_id` nor a `placed_cube_instances.object_id` and whose parent cell holds no placed cube (rubble of a lost override, which the load-time heal would otherwise use to remove the restored cell again; the debris of a broken player cube in an air cell has the same shape and goes too, which is harmless). Both deletions count in `RubbleRowsDeleted`. Cells under a saved placed cube keep their override and rubble and are counted in `CellsKeptForPlacedCubes`. `worlds.next_object_id` is never lowered. The base is the world's own: the generator output for a generated world, the stored chunks for a stored world. A player or enemy saved inside a restored cell is not moved, but is reported: `TerrainResetResult.PlayersInside`, `EnemiesInside` and `PlayerNames` (a position is inside a cell when its floor is that cell), printed as `inside=` and `inside_players=`.

**Offline only.** Export and reset are for a world that no server is running. A running server's later checkpoint, or its restore heal, could write the rows again, so since wave F3 both commands (like `copy` and `delete`) take the world's host lease and refuse a held world with exit code 7. The lease is a file, `{db}.world-{id}.lock`, locked by the operating system; it adds no table or column (the schema stays at version 6).

## Phase 5: lease, integrity check, backup (wave F3)

No schema change. `WorldHostLease` (`Server/Hosting`) holds one exclusive `FileStream` per world; a crash frees it. `WorldIntegrityCheck` (`Server/Persistence`) runs `quick_check`, `foreign_key_check`, bounds, slot and kind checks when a world loads; foreign-key violations or corruption stop the start with exit code 6, the rest is logged with counts. `--db backup <path>` uses `SqliteConnection.BackupDatabase` (safe while running); `--db restore <path> --yes` swaps the file offline after validating the source (version not newer, `integrity_check`) and keeps the old file as `{db}.pre-restore.{timestamp}.bak`. Details and exit codes are in `docs/configuration.md`.

## Accounts and characters (migration 0007, wave G)

Schema version 7 (plan `docs/ledger/plans/2026-10-31-wave-g-plan.md`). `accounts` holds `account_id` (AUTOINCREMENT), `token_hash` (BLOB, `CHECK (length(token_hash) = 32)`, unique), `created_utc` and `last_seen_utc`. The hash is the SHA-256 of the player's 32-byte token; the token itself is never stored, so a database backup holds no usable secret. `player_profiles` gains `account_id INTEGER NULL REFERENCES accounts(account_id) ON DELETE SET NULL` (partial index `ix_player_profiles_account_id`). A character with `account_id IS NULL` is unowned (a legacy character, or one released with `--characters release`); the first token to join it claims it, up to 5 characters per account. `player_name` stays the immutable key. `display_name` is what people type and see, and is unique ignoring ASCII case (`ux_player_profiles_display_name`, `COLLATE NOCASE`). Migration 0007 renames duplicate display names to `<name>~<n>`; the `.bak` taken before it holds the originals.

`SqliteAccountRepository` (`Server/Persistence/Repositories`) implements `IAccountRepository`. `Lookup` finds the row whose `display_name` matches ignoring ASCII case (returning its key, not the typed text), else whether a row has that exact `player_name`, and counts the characters the account owns. `RecordJoin` upserts the account (moving `last_seen_utc`, keeping `created_utc`) and, when claiming, sets `account_id` only where it is NULL, returning false when no row changed. `SqlitePlayerProfileRepository.Save` never names `account_id`, so ordinary saves cannot change ownership. `--worlds copy` and `export` copy per-world tables only, so `accounts` and `player_profiles` are never copied. `--db restore` brings back whatever accounts the restored file holds.

`--characters list | release <name> | rename <old> <new>` (`Server/Characters/CharacterCli.cs`) is tab-separated: `list` prints display name, key, level, `account_id` (or `-`) and the account's `last_seen_utc`, never a hash. `release` and `list` take no lease and run beside a server; `rename` changes `display_name` only, needs every world's lease (exit 7 when held) and refuses a name that is taken or equals another character's key (exit 2) or fails the name rule (exit 1). Other exit codes: 0 ok, 3 schema too new. Control characters print as `?`.

The integrity check adds `AccountsOverLimit` (accounts with more than 5 characters) and `DuplicateDisplayNames` (both soft, logged) and `UnownedCharacters` (information in the head line, not a finding). A bad `account_id` is already fatal through `foreign_key_check`.

## Design Notes

- The schema is intentionally denormalized.
- Restore speed and straightforward authoritative writes matter more than strict normalization.
- Player identity is currently keyed by `player_name`.
- Inventory ownership uses item rows plus slot-assignment rows.
- The server remains the only authority that should write gameplay state into these tables.
- Authoritative player combat formulas are now persisted as editable expression strings in SQLite.

## Tables

### `player_profiles`

One row per persistent player identity.

Key fields:

- `player_name`: primary key
- `display_name`: current multiplayer-visible name
- `interaction_mode`: `Building` or `Combat`
- combat stat columns: `strength`, `agility`, `vitality`, `energy`
- movement/combat tempo: `movement_speed`, `attack_speed`
- progression and health: `level`, `current_hp`, `max_hp`
- progression tracking: `level`, `experience`, `current_hp`, `max_hp`
- timestamps: `created_utc`, `updated_utc`

Used for:

- creating or restoring a named player profile at join time
- persisting authoritative base stat and mode changes
- reconnect restore of long-lived character state
- reconnect restore of long-lived character progression state

Important note:

- effective combat stats from equipped tools are not stored here
- they are recomputed on the server from base stats plus equipped inventory state
- current XP is stored here, while required next-level XP is computed from server formulas

### `player_world_state`

One row per player position/facing checkpoint.

Key fields:

- `world_id`, `player_name`: composite primary key; `player_name` is an FK to `player_profiles`
- `position_x`, `position_y`, `position_z`
- `facing_x`, `facing_y`, `facing_z`
- `current_hp`
- `current_energy`
- `updated_utc`

Used for:

- reconnect restore of map position and facing
- periodic 5-second authoritative checkpoints

Checkpoints are written off the tick thread in one transaction by `SqlitePersistenceCheckpointWriter`. It upserts `player_world_state`, but only updates existing `placed_cube_instances`, `cube_fracture_pieces` and `enemy_instances` rows, plus the profile's `interaction_mode`, `current_hp` and `max_hp`. Inserts and deletes for those tables stay on the immediate gameplay paths: placement, fracture, enemy spawn and death. See `docs/performance-and-optimizations.md`.

**The checkpoint `updated_utc` guard.** A player's leave (`WorldSessionRuntime.DetachRemoteSession`) also writes `player_world_state` and the profile, through the write queue, while checkpoints are written by their own worker. Before wave A, a checkpoint captured before the leave but written after it overwrote what the leave saved, because the writer stamped every row with its own write time. Since wave A (workstream A4) both player row kinds are guarded, and both guards anchor on the `player_world_state` row:

- `player_world_state` (`SqlitePlayerWorldStateRepository.CheckpointUpsertSql`): the checkpoint row's `updated_utc` is its capture time (`PersistedPlayerWorldState.UpdatedUtc`), and the upsert's conflict branch ends with `WHERE player_world_state.updated_utc < excluded.updated_utc`, so it replaces only a strictly older row. A first insert (no row yet) still inserts.
- Profile vitals (`interaction_mode`, `current_hp`, `max_hp`; `SqlitePlayerProfileRepository.CheckpointUpdateVitalsSql`): the update is skipped when the player's world-state row is strictly newer than the capture: `... WHERE player_name = $playerName AND NOT EXISTS (SELECT 1 FROM player_world_state WHERE player_world_state.player_name = $playerName AND player_world_state.updated_utc > $capturedUtc)`, with `$capturedUtc` from `PlayerCheckpointVitals.CapturedUtc`. Only a leave and checkpoints write that row, so a newer row means the player left after the capture and the profile the leave saved must stand. Vitals with the default `CapturedUtc` bind `NULL`, which makes the comparison `NULL`: they are unguarded, as before.
- `RepositoryPersistenceCheckpointWriter`, the fallback writer, applies the same two rules by reading the stored world-state row first.

**Why the vitals do not compare the profile's own `updated_utc`.** Every immediate profile save (XP, level, stats, mode) is queued and stamps the worker's time when it runs. A save queued before a capture but run after it would then make that capture's vitals skip, depending only on how far the write queue lagged the tick, so two servers running the same script could persist different HP. The world-state row is written only by the leave and by checkpoints, both stamped with a time taken on the tick thread, so the guard is deterministic.

**Timestamps.** The vitals update still stamps the profile's `updated_utc` with the checkpoint's write time; the capture time is only compared. `player_world_state.updated_utc` carries a capture time: the checkpoint's, or for a leave the time taken on the tick thread when the session detaches (`RuntimePersistenceBridge.PersistPlayerWorldState`; `SqlitePlayerWorldStateRepository.SavePlayerWorldState` binds `state.UpdatedUtc` and falls back to the write time only for a default value). Capture and leave both run on the tick thread, so a capture taken before a leave is never newer than the leave's row, and a capture taken after a leave no longer contains the player.

**Save order on leave.** The vitals guard holds only once the leave's world-state row is committed, so `DetachRemoteSession` queues the world state *before* the profile. A late checkpoint then either commits before both (the leave's profile save overwrites its vitals) or after the world state (its vitals are skipped). With the profile queued first, a checkpoint committing between the two writes found no newer world-state row and overwrote the leave's HP with its stale vitals. `CheckpointLeaveRaceGuardTests` pins both orders.

**Text comparison.** `FormatUtc` writes the round-trip ("O") format in UTC, a fixed 28 characters, so comparing the text compares the times. Older `strftime` defaults (3 fractional digits, then `Z`) compare as newer than any 7-digit value within the same millisecond, which errs towards keeping the stored row.

**Limit: the wall clock moving backwards.** The times are `DateTime.UtcNow`. If the clock steps back (a manual change, an NTP correction), a row stamped before the step is in the future: until real time passes it again, checkpoints skip that player's world-state row and vitals, so a crash in that window loses up to that much of the player's position and HP. The player's next leave repairs it, since a leave's writes are not guarded.

The guard covers only these two row kinds. Checkpoint updates of enemies, placed cubes, fracture pieces and `world_time_ticks` are unchanged.

Immediate gameplay writes (placed cubes, fracture pieces, enemies, profiles, inventory) go through an ordered `PersistenceWriteQueue`. It runs on a background worker on the dedicated server, and inline by default for tests and tools. The tick never reads these tables during gameplay: placed-cube identity repair uses an in-memory index loaded at startup and kept current by saves, deletes and checkpoints, and profile lookups use a cache filled at join. Join flushes the queue before reading the joining player's rows.

### `placed_cube_instances`

One row per surviving player-made cube.

Key fields:

- `world_id`, `cube_id`: composite primary key
- `owner_player_name`: FK to `player_profiles`
- `cell_x`, `cell_y`, `cell_z`: coarse grid cell that owns the cube (`INTEGER`)
- `center_x`, `center_y`, `center_z`: nullable exact render/physics center; older rows may be null and restore from the coarse cell center
- `shape_kind`
- `material_id`
- `current_health`, `max_health`
- timestamps

Used for:

- startup world restore
- cube placement persistence
- pushed dynamic cube reconnect restore
- cube destruction and fracture cleanup

### `cube_fracture_pieces`

One row per persisted fragment produced when a destructible cube fractures.

Key fields:

- composite PK: `world_id`, `source_cube_id`, `piece_index`
- `owner_player_name`: original cube owner, FK to `player_profiles`
- `parent_cell_x`, `parent_cell_y`, `parent_cell_z`: coarse cell that owns the rubble group (`INTEGER`)
- `center_x`, `center_y`, `center_z`: authoritative fragment center in world coordinates
- `side_length`: fragment cube size
- `material_id`: material used by render and collision rehydration
- timestamps

Used for:

- restoring destroyed-cube rubble on server startup or client rejoin
- keeping render and collision fragments aligned after authoritative physics updates
- preserving ownership/material metadata after the source cube leaves `placed_cube_instances`

Important note:

- the source cube row is deleted when it fractures
- the rubble pieces become the persistent world representation for that cell
- the runtime may keep a hidden compatibility cell so map queries can find the rubble group, but render/collision export uses the stored fragment pieces

### `enemy_instances`

One row per currently active enemy instance.

Key fields:

- `world_id`, `enemy_id`: composite primary key
- `enemy_kind`
- `display_name`
- `position_*`, `facing_*`
- `current_hp`, `max_hp`
- creature stats and tempo columns
- `level`
- timestamps

Used for:

- authoritative enemy startup restore
- HP/position/facing checkpoints
- despawn and death cleanup

### `item_instances`

One row per persistent item instance.

Key fields:

- `world_id`, `item_instance_id`: composite primary key (item ids are unique per world)
- `item_kind`
- `display_name`
- `visual_id`
- `owner_player_name`: nullable FK to `player_profiles`
- `world_position_*`: nullable world-drop coordinates
- `state`: current ownership/state marker
- `stack_count`
- timestamps

Used for:

- player inventory contents
- future world drops and pickups
- held/equipped-item visual resolution through `visual_id`

### `player_inventory_slots`

One row per occupied player inventory or equipped slot.

Key fields:

- composite PK: `world_id`, `player_name`, `slot_index`
- `item_instance_id`, with `(world_id, item_instance_id)` an FK to `item_instances`
- `updated_utc`

Used for:

- 4x4 backpack slot assignment
- dedicated equipped-slot assignment
- reconnect restore of tool selection

### `player_starter_grants` (migration 0002)

One row per player and world. It records which version of the starter kit the player has received in that world. Migration 0003 added `world_id` and made the primary key `(world_id, player_name)`; the description below is of the 0002 shape plus that.

Columns:

- `world_id INTEGER NOT NULL` (0003), and `player_name TEXT NOT NULL`, together the primary key; `player_name` references `player_profiles(player_name)` `ON DELETE CASCADE`
- `kit_version INTEGER NOT NULL`, with `CHECK >= 0`
- `updated_utc TEXT NOT NULL`, the write time

The inventory repository owns this table. `ToolCatalog.StarterKit` versions the kit:

| Kit version | Adds |
| --- | --- |
| 1 | hammer, blade, maul, lantern |
| 2 | torch |

How grants work:

- `PlayerInventoryService` grants only the kinds introduced after the player's recorded version. An item a player gets rid of is therefore never granted again, even into an empty backpack.
- Migration `0002_player_starter_grants.sql` only creates the table; it does no backfill.
- A player with inventory rows but no grant row is treated as having kit v1. They receive the missing later kinds (the torch) once, kinds they already hold are skipped, and the current version is recorded.
- A new player with an empty backpack and no row receives the whole kit, and the current version is recorded.

How the table is read and written:

- `LoadPlayerInventory` reads the grant row in its single statement, through a `UNION ALL` on the grant table's primary key. The join prefetch therefore gets slots, items and kit version from one snapshot.
- `SaveStarterKitGrant` is an upsert that never lowers the stored version. It is queued right after the writes for the granted items, so it lands in the same write-queue group commit.
- `DeleteInventorySlots` leaves the grant row in place.
- Upgrading a version 1 database to 0002 takes the usual `{db}.v0001-to-v0002.{timestamp}.bak` backup.

### `combat_formulas`

One row per authoritative combat formula expression.

Key fields:

- `formula_key`: stable formula id such as `player.max_health`
- `expression_text`: arithmetic expression string used by the authoritative server
- `description`: plain-language note for the formula
- `enabled`: active/inactive toggle
- `updated_utc`

Used for:

- authoritative player stat derivation
- rebalance of HP, damage, crit, and attack cadence without recompiling
- rebalance of XP thresholds and enemy XP rewards without recompiling
- server startup validation of required combat-rule rows

Important note:

- the client does not authoritatively evaluate these expressions
- the server loads, validates, and evaluates them through a strict whitelisted arithmetic engine
- the default rows are seeded once, by the `0001_baseline` migration; a deleted seed row is not restored on restart (see Schema Versions And Migrations)

## Indexes

The baseline adds lookup indexes (migration 0003 recreates the per-world ones under the same names, with `world_id` leading where noted under Worlds, and adds `ux_worlds_name`) for:

- recently updated player profiles
- recently updated player world state
- cube ownership and update time
- cube fracture ownership, parent cell, and update time
- enemy update time
- item ownership and update time
- slot lookup by `item_instance_id`
- player profile change history by player and time

These indexes are aimed at:

- fast startup restore scans
- direct player-name lookups
- efficient authoritative checkpoint writes

## Triggers

`trg_player_profiles_set_updated_utc` keeps `player_profiles.updated_utc` fresh on update when callers do not explicitly change the timestamp value.

## world_metadata

A generic key/value table, created by the `0001_baseline` migration (`CREATE TABLE IF NOT EXISTS`):

- `key TEXT NOT NULL PRIMARY KEY`
- `value TEXT NOT NULL`

It is global, not per world, and it holds no rows today. Before schema version 3 it held the world's `simulation_seed` and `world_time_ticks`; migration 0003 moved both onto the `worlds` row of world 1 (columns `seed` and `world_time_ticks`) and deleted the two keys. See Worlds above.

## player_profile_change_log

Append-only history of profile stat and mode changes (`change_kind`, previous and new `interaction_mode`, `strength`, `attack_speed` and `vitality`, `changed_utc`), keyed by `player_name` with `ON DELETE CASCADE`. The table and its index are created by the baseline, but no code or trigger writes to it today, so it stays empty.

## Connection Settings, WAL And Write Path

`SqliteSchemaBootstrapper` (`dream/Server/Persistence/SqliteSchemaBootstrapper.cs`) configures every connection:

- `EnsureSchema` runs `PRAGMA journal_mode=WAL` before it migrates. The mode is stored in the database file. Every later connection opens in WAL mode, so readers do not block the writer.
- Each connection sets `synchronous=NORMAL`, `busy_timeout=5000` and `journal_size_limit=67108864`. `NORMAL` syncs at WAL checkpoints instead of on every commit: a process crash cannot corrupt the database, but a power loss or OS crash can lose the last few commits. That trade-off is accepted for game state. The `-wal` file is truncated back to 64 MB after a checkpoint.
- The connection string sets `DefaultTimeout = 5`, so a statement on a locked database waits about 5 s rather than the 30 s default.

Writes reach SQLite in three ways, all off the tick thread on the dedicated server:

- **Immediate writes** go through `PersistenceWriteQueue` (`Dream.Simulation.Persistence`). One worker takes every write queued so far (up to `MaxWritesPerGroup`, 256) and runs them in one `SqliteWriteBatch` transaction with a savepoint per write (group commit): a failing write rolls back to its savepoint and the rest commit. Reads end a group. When the database is locked past `busy_timeout`, the unrun writes go back to the head of the queue and the worker backs off from 100 ms to 2 s.
- **Checkpoints** are captured under the authority lock every 5 s, mostly as incremental deltas (a full sweep every 12th capture and after a map rebuild), and written by `BackgroundCheckpointWriter` through `SqlitePersistenceCheckpointWriter`.
- **Shutdown** shares one drain deadline, `PersistenceWriteQueue.DefaultDisposeDrainDeadline` (30 s), for the queue and the final full checkpoint.

The details and measurements are in `docs/performance-and-optimizations.md`, Checkpoint Persistence Off The Tick.

## Current Inventory Convention

The gameplay inventory slice uses:

- backpack slots `0..15`
- one equipped slot stored separately
- item definitions resolved in code by `item_kind`
- per-item held-viewmodel selection resolved by `visual_id`

## Migration Guidance

When extending this schema:

- add a new `dream/config/migrations/NNNN_snake_case.sql` with the next version; never edit a migration that has shipped, and never edit `player-profiles.init.sql`
- write the script for the schema of the version before it, without `BEGIN`, `COMMIT`, `ROLLBACK`, `END TRANSACTION` or a journal-mode pragma
- prefer additive columns and additive tables
- keep startup restore queries direct
- a new per-world table gets `world_id` first in its primary key and a `FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE`; add it to the world catalog's copy and delete lists
- when a migration rebuilds a table, use the 12-step procedure (never rename the old table away first); the migrator keeps foreign keys off and checks `foreign_key_check` at the end
- avoid collapsing players, enemies, cubes, and items into one generic entity table
- keep formula evaluation server-owned even when the source strings live in the DB
- document new runtime assumptions in the ledger and here together
