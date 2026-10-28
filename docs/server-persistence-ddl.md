## Purpose

Document the authoritative SQLite schema that backs player profiles, world state, enemies, placed cubes, items, and inventory.

## Source Of Truth

The ordered migration scripts in [dream/config/migrations](/G:/workspace/projects/dream/dream/config/migrations) are the source of truth for the schema (persistence wave A, plan `docs/ledger/plans/2026-10-28-wave-a-plan.md`). `0001_baseline.sql` is the schema every existing database already has: the statements of `player-profiles.init.sql` byte for byte, plus the `world_metadata` table. Every later change is a new `NNNN_snake_case.sql` file.

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

- `player_name`: primary key and FK to `player_profiles`
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

- `cube_id`: primary key
- `owner_player_name`: FK to `player_profiles`
- `cell_x`, `cell_y`, `cell_z`: coarse grid cell that owns the cube
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

- composite PK: `source_cube_id`, `piece_index`
- `owner_player_name`: original cube owner, FK to `player_profiles`
- `parent_cell_x`, `parent_cell_y`, `parent_cell_z`: coarse cell that owns the rubble group
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

- `enemy_id`: primary key
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

- `item_instance_id`: primary key
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

- composite PK: `player_name`, `slot_index`
- `item_instance_id`
- `updated_utc`

Used for:

- 4x4 backpack slot assignment
- dedicated equipped-slot assignment
- reconnect restore of tool selection

### `player_starter_grants` (migration 0002)

One row per player. It records which version of the starter kit the player has received.

Columns:

- `player_name TEXT NOT NULL PRIMARY KEY`, referencing `player_profiles(player_name)` `ON DELETE CASCADE`
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

The baseline adds lookup indexes for:

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

Key/value metadata about the persisted world. It is created by the `0001_baseline` migration (`CREATE TABLE IF NOT EXISTS`), so databases that predate it gain it when they migrate to version 1.

- `key TEXT NOT NULL PRIMARY KEY`
- `value TEXT NOT NULL`

It holds two rows:

- `simulation_seed`: the world's simulation seed stored as a decimal unsigned 64-bit number. `SqliteWorldMetadataRepository` inserts it once with `INSERT OR IGNORE` and a non-numeric value is treated as a startup error.
- `world_time_ticks`: the world clock (`WorldClock.WorldTicks`, authoritative ticks since the world was created) as a decimal non-negative 64-bit number. `SqlitePersistenceCheckpointWriter` upserts it inside each checkpoint transaction whose `PersistenceCheckpoint.WorldTimeTicks` is set (every non-null capture, so idle incremental captures write nothing), and `ServerBootstrap.CreatePersistenceBundle` reads it with `TryGetWorldTimeTicks`. A missing row means a new world (0); a non-numeric value is a startup error.

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
- avoid collapsing players, enemies, cubes, and items into one generic entity table
- keep formula evaluation server-owned even when the source strings live in the DB
- document new runtime assumptions in the ledger and here together
