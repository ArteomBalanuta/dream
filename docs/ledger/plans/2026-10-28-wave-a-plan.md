# Persistence Wave A Plan: SQL Foundation

Purpose: split persistence wave A of `2026-10-28-world-persistence-roadmap.md` into a contract commit (C0) and five workstreams that touch disjoint files.

Status: approved for implementation. The plan was analysed read-only on `integrate/engine-iteration` at `951dc74`. It runs in parallel with day/night wave T2, which touches none of these files.

## What The Code Does Today

### Schema setup

`SqliteSchemaBootstrapper.EnsureSchema()` runs on every startup and does four things:

1. Runs all of `config/player-profiles.init.sql`. The script is idempotent: `IF NOT EXISTS` everywhere, and the formula seed uses `ON CONFLICT DO NOTHING`.
2. Creates `world_metadata` ad hoc.
3. Calls `EnsureColumnExists` four times: `player_profiles.experience` and `placed_cube_instances.center_x`, `center_y` and `center_z`.
4. Sets `journal_mode=WAL`, outside any transaction.

"Today's schema" is therefore `init.sql` plus `world_metadata`. `init.sql` already contains the four columns.

### Compatibility constraints

- About 18 test files call `new SqliteSchemaBootstrapper(db, "<repo>/dream/config/player-profiles.init.sql")`, then `EnsureSchema()`. Both must stay source-compatible.
- All repository calls go through `SqliteSchemaBootstrapper.OpenConnection()`, which calls `SqliteWriteBatch.TryEnlist(schema)` for group commit.
- The checkpoint writer is the exception: it opens its own connection, calls `BeginTransaction()` (IMMEDIATE), and never enlists. It also runs as a queue read during the final checkpoint, so it must never enlist.
- Eleven test fakes implement `IWorldPersistenceRepository` implicitly, and tests use `nameof(IWorldPersistenceRepository.X)`. The facade can therefore become an interface that inherits the per-aggregate interfaces, with no test changes.
- Tests use `SqliteWorldPersistenceRepository(schema)`, `.LoadPlayerInventorySql` and `SqliteConnectionLease`. All three must stay.

### The leave and checkpoint race (a known limit from wave 13)

| Write | When the time is taken | Notes |
| --- | --- | --- |
| Leave, world state | `PersistPlayerWorldState` sets `UpdatedUtc = DateTime.UtcNow` at leave time | `SavePlayerWorldState` ignores it and writes `UtcNow` again when the queue worker runs (call that T_w, with T_w ≥ T_leave) |
| Leave, profile | `PersistPlayerProfile` → `SaveProfile` → queued `Save` | Also writes `UtcNow` at worker time |
| Checkpoint capture | `now = DateTime.UtcNow` on the tick thread | World-state rows get `UpdatedUtc = now` (T_c). `PlayerCheckpointVitals` carries no timestamp |
| Checkpoint write | `SqlitePersistenceCheckpointWriter.Write` | Stamps every row with `UtcNow` at write time, with no guard |

A checkpoint captured before a leave but written after it therefore overwrites what the leave saved.

### Accepted behaviour change

With migrations, the baseline runs once instead of on every startup. A deleted `combat_formulas` seed row no longer comes back on restart; edited formulas were already kept. This is documented by A5.

## Split

There is one contract commit, C0, plus five workstreams. No file is shared between workstreams.

Change from the roadmap: the migration tests move into A1. A5 becomes the safety net: characterization tests, dependency rules and docs. A5 merges first.

C0 ships the store implemented, not as stubs. After C0, A2 owns the store files, and their API is frozen: only bodies and comments may change. C0 is the only commit that edits `IWorldPersistenceRepository.cs`. C0 also adds the `partial` keyword to `SqlitePlayerProfileRepository` and `SqliteWorldMetadataRepository`.

## C0 Contract Files

### Store

Location: `dream/Server/Persistence/Store/`, namespace `Dream.Server.Persistence.Store`.

```csharp
public interface IWorldStore
{
    SqliteSchemaBootstrapper Schema { get; }
    // Same semantics as SqliteSchemaBootstrapper.OpenConnection: inside an active SqliteWriteBatch on
    // this thread the session runs on the batch's connection, transaction and current savepoint
    // (IsEnlisted); otherwise it owns a fresh autocommit connection. Disposing never commits a batch.
    SqliteSession OpenSession();
    // Never enlists: an owned connection plus connection.BeginTransaction() (IMMEDIATE, as today's
    // checkpoint writer). Commit() commits; Dispose without Commit rolls back.
    SqliteSession BeginOwnedTransaction();
}

public sealed class SqliteWorldStore(SqliteSchemaBootstrapper schema) : IWorldStore { /* OpenSession:
    SqliteWriteBatch.TryEnlist(schema) ?? schema.CreateOpenConnection() */ }

public sealed class SqliteSession : IDisposable
{
    public SqliteConnection Connection { get; }
    public SqliteTransaction? Transaction { get; }   // non-null only for BeginOwnedTransaction
    public bool IsEnlisted { get; }
    public SqliteCommand CreateCommand(string sql);  // sets CommandText and Transaction (if owned)
    public void Commit();                            // owned transaction only; else InvalidOperationException
    public void Dispose();                           // rolls back an uncommitted owned tx; closes an owned connection
}

public static class SqliteCommandExtensions      // no allocations beyond what AddWithValue costs today
{
    public static SqliteParameter AddParameter(this SqliteCommand c, string name, SqliteType type); // reusable, prepared
    public static void BindText(this SqliteCommand c, string name, string value);
    public static void BindInt64(this SqliteCommand c, string name, long value);
    public static void BindReal(this SqliteCommand c, string name, double value);
    public static void BindNullableText(this SqliteCommand c, string name, string? value);
    public static void BindUtc(this SqliteCommand c, string name, DateTime value);  // SqliteSchemaBootstrapper.FormatUtc
    public static void BindVector3(this SqliteCommand c, string x, string y, string z, Vector3 v);
    public static void BindNullableVector3(this SqliteCommand c, string x, string y, string z, Vector3? v);
    public static Vector3 GetVector3(this SqliteDataReader r, int firstOrdinal);           // floats at o, o+1, o+2
    public static Vector3? GetNullableVector3(this SqliteDataReader r, int firstOrdinal);  // null when o is NULL
    public static DateTime GetUtc(this SqliteDataReader r, int ordinal);                   // ParseUtc
    public static string? GetNullableString(this SqliteDataReader r, int ordinal);
}

public abstract class SqliteRepositoryBase(IWorldStore store)
{
    protected IWorldStore Store { get; } = store;
    // State-passing overloads so callers can use static lambdas (no closure allocation on hot paths).
    protected int Execute<TState>(string sql, TState state, Action<SqliteCommand, TState> bind);
    protected T? QuerySingle<TState, T>(string sql, TState state, Action<SqliteCommand, TState> bind,
        Func<SqliteDataReader, T> read) where T : struct;
    protected List<T> QueryList<T>(string sql, Func<SqliteDataReader, T> read);
    protected long QueryInt64(string sql);
}
```

Each method opens one session per call, the same as today's one lease per call.

### Migrations

Location: `dream/Server/Persistence/Migrations/`, namespace `Dream.Server.Persistence.Migrations`.

```csharp
public interface ISchemaMigration
{
    int Version { get; }          // 1-based, contiguous
    string Name { get; }          // from the file name
    void Apply(SqliteConnection connection, SqliteTransaction transaction);
}

public interface ISchemaMigrator
{
    int LatestVersion { get; }
    int GetCurrentVersion(SqliteConnection connection);      // 0 when schema_version is absent
    // Applies pending migrations in ONE transaction. Throws SchemaTooNewException when the stored version
    // > LatestVersion (file untouched). Backs the file up first when migrations are pending AND the
    // database already had user tables. Returns what it did.
    SchemaMigrationResult Migrate(string databasePath, SqliteConnection connection, Action<string>? log = null);
}

public sealed record SchemaMigrationResult(int FromVersion, int ToVersion, string? BackupPath);
public sealed class SchemaTooNewException(int stored, int latest) : InvalidOperationException(...);
public static class SchemaMigrations
{
    public static IReadOnlyList<ISchemaMigration> LoadFromDirectory(string directory);  // validated
}
```

- **Files.** Migrations live in `dream/config/migrations/NNNN_snake_case.sql` and must match `^(\d{4})_([a-z0-9_]+)\.sql$`. Versions start at 1 and are contiguous; a gap or a duplicate fails startup.
- **Directory.** `Path.Combine(Path.GetDirectoryName(schemaScriptPath), "migrations")`. The existing `config\**\*` copy glob ships it.
- **Script content.** Scripts contain no `BEGIN`, `COMMIT` or `PRAGMA journal_mode`.
- **Version table.** `CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL PRIMARY KEY, name TEXT NOT NULL, applied_utc TEXT NOT NULL);`, with one row per applied migration.
- **Backup.** Named `{db}.v{from:D4}-to-v{to:D4}.{yyyyMMddTHHmmssZ}.bak` and made with `SqliteConnection.BackupDatabase`.

### Per-aggregate interfaces

Location: `dream/Simulation/Persistence/`. Signatures and doc comments move unchanged from `IWorldPersistenceRepository`.

| Interface | Methods |
| --- | --- |
| `IPlayerWorldStateRepository` | `TryGetPlayerWorldState`, `SavePlayerWorldState` |
| `IPlacedObjectRepository` | `LoadPlacedCubes`, `SavePlacedCube`, `DeletePlacedCube` |
| `IRubbleRepository` | `LoadCubeFracturePieces`, `SaveCubeFracturePiece`, `DeleteCubeFracturePieces` |
| `IEnemyRepository` | `LoadActiveEnemies`, `SaveEnemyInstance`, `DeleteEnemyInstance`, `ReassignEnemyId` |
| `IItemRepository` | `LoadItemInstances`, `SaveItemInstance`, `DeleteItemInstance`, `GetMaxItemInstanceId` (also reads the slot table, as today) |
| `IInventoryRepository` | `LoadInventorySlots`, `LoadPlayerInventory`, `SaveInventorySlot`, `DeleteInventorySlots` |

```csharp
public interface IWorldPersistenceRepository : IPlayerWorldStateRepository, IPlacedObjectRepository,
    IRubbleRepository, IEnemyRepository, IItemRepository, IInventoryRepository { }
```

C0 also adds Sqlite class skeletons in `dream/Server/Persistence/Repositories/`:

- `SqlitePlayerWorldStateRepository`
- `SqlitePlacedObjectRepository`
- `SqliteRubbleRepository`
- `SqliteEnemyRepository`
- `SqliteItemRepository`
- `SqliteInventoryRepository`

Each is declared as `public sealed partial class X(IWorldStore store) : SqliteRepositoryBase(store), I…`, with bodies that throw `NotImplementedException("A3")`.

## The `updated_utc` Guard (A4)

**Rule:** a checkpoint row carries its capture time, never its write time. It may overwrite a stored row only when the stored row is strictly older than that capture.

**Rows guarded:** only `player_world_state` and the profile vitals (`player_profiles` `interaction_mode`, `current_hp` and `max_hp`). Updates to enemies, cubes and rubble, and to `world_time_ticks`, are unchanged.

**Capture times:**
- World-state rows already carry `PersistedPlayerWorldState.UpdatedUtc = now`, the capture time.
- Add `public DateTime CapturedUtc { get; init; }` to `PlayerCheckpointVitals`, set to `now` in `CaptureConnectedPlayers`. The default value means "unguarded, stamp `UtcNow`".
- `PersistenceCheckpoint.Merge` keeps whole rows per key, so each row keeps its own time.

**SQL for `player_world_state`.** Keep the insert-if-profile-exists upsert. Set `$updatedUtc` per row to `FormatUtc(state.UpdatedUtc)`, and guard the conflict branch:

```sql
ON CONFLICT(player_name) DO UPDATE SET …, updated_utc = excluded.updated_utc
WHERE player_world_state.updated_utc < excluded.updated_utc;
```

**SQL for vitals:**

```sql
UPDATE player_profiles SET interaction_mode=$m, current_hp=$hp, max_hp=$max, updated_utc=$capturedUtc
WHERE player_name=$playerName AND updated_utc < $capturedUtc;
```

Strict `<` means the new value always differs from the old, so `trg_player_profiles_set_updated_utc` never fires.

**Why this is correct:**
- Capture and leave both run on the tick thread, so a capture taken before a leave has T_c ≤ T_leave ≤ T_w, and that row is skipped.
- A capture taken after the leave does not contain the departed player.
- `FormatUtc` ("O" format, UTC) is a fixed 28 characters, so comparing the text compares the times.
- Legacy `strftime` values (3 fractional digits) compare as "newer" only within the same millisecond, which is the conservative direction.

**Accepted edge case.** A profile `Save` that is queued before a capture but runs after it stamps a later time, so the next checkpoint skips those vitals. A4 documents this in a comment.

**Fallback writer.** `RepositoryPersistenceCheckpointWriter` follows the same rule. It skips the world state when `TryGetPlayerWorldState(..).UpdatedUtc >= state.UpdatedUtc`, and the vitals when `profile.UpdatedUtc >= vitals.CapturedUtc`, but only when `CapturedUtc != default`.

**Comments.** Fix the stale comment in `SqlitePersistenceCheckpointWriter` ("only the checkpoint writes player_world_state") and the doc comment in `IPersistenceCheckpointWriter`.

## Workstreams

No file belongs to more than one workstream. No workstream touches:
- `SqliteWriteBatch.cs`, `PersistenceWriteQueue.cs`, `RuntimePersistenceBridge.cs` or `ServerPersistenceBundle.cs`;
- any file wave T2 owns;
- another workstream's C0 file.

### A1: Migration runner

**Owns:**
- `Server/Persistence/SqliteSchemaBootstrapper.cs`:
  - `EnsureSchema(Action<string>? log = null)` sets the WAL pragma, then runs the migrator.
  - `EnsureColumnExists` is removed.
  - The constructor, `OpenConnection`, `CreateOpenConnection`, `FormatUtc`/`ParseUtc` and `SqliteConnectionLease` stay.
- The C0 migration types, implemented as `Migrations/SqliteSchemaMigrator.cs`, `SqlScriptMigration.cs` and `BaselineMigration.cs`.
- `config/migrations/0001_baseline.sql`: the `init.sql` statements byte-for-byte, plus `CREATE TABLE IF NOT EXISTS world_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);`.
- `config/player-profiles.init.sql`: a header comment only ("frozen pre-migration schema; tests use it as the legacy fixture").
- The one line in `ServerBootstrap.CreatePersistenceBundle` that becomes `schema.EnsureSchema(log)`.
- New tests:
  - `dream.Tests/Server/Persistence/Migrations/SchemaMigrationTests.cs`;
  - `LegacySchemaMigrationTests.cs`;
  - a helper, `SqliteSchemaSnapshot.cs`, that normalizes `sqlite_master` plus `table_info`, `index_list` and the triggers;
  - the fixture `Fixtures/legacy-pre-center-experience.sql`: `init.sql` without the `center_*` columns, `experience` or `world_metadata`.

`BaselineMigration` runs the idempotent script, then backfills the four columns exactly as today. It is the only migration that probes columns, and it runs only from version 0 to 1.

**Tests:**
1. A fresh database equals the snapshot of `init.sql` plus `world_metadata`, with column order, and `schema_version` = {1}.
2. A database built with the old script and holding rows in every table migrates to v1. The rows are unchanged, the snapshot is equal, and a backup is made.
3. The pre-columns fixture with rows gains the columns (`experience` = 0, `center_*` NULL) and is equal ignoring column order. The seed is preserved.
4. A second `EnsureSchema` is a no-op: no backup, no new version row.
5. A stored version of 99 throws `SchemaTooNewException`, and the file's bytes are unchanged.
6. An injected failing migration 0002 rolls back, the version stays at 1, and the backup exists.
7. A gap or a duplicate in the migration directory throws.
8. A WAL database stays WAL.

**Must not touch:** repositories, the checkpoint writer, the store, docs.

### A2: Store and the non-world repositories

**Owns:**
- After C0: the bodies in `Store/*.cs`. Their API is frozen.
- `SqlitePlayerProfileRepository.cs`, `SqliteCombatFormulaRepository.cs` and `SqliteWorldMetadataRepository.cs` (the main files only). They move onto `SqliteRepositoryBase` and keep the same public constructors, which build `new SqliteWorldStore(schema)`. Their constants stay public.
- `dream.Tests/Server/Persistence/Store/SqliteStoreTests.cs`.

**Tests:**
- Inside an active `SqliteWriteBatch`, `OpenSession` enlists, and N writes give `CommittedTransactions == 1`.
- Outside a batch, the session owns its connection.
- An owned transaction rolls back on dispose and commits on `Commit`.
- `Commit` on an enlisted session throws.
- The helpers round-trip NULL, `Vector3?`, UTC and long values.
- A savepoint rollback undoes only the failing write.

**Must not touch:** the `*.Checkpoint.cs` partials (A4), the bootstrapper, `SqliteWriteBatch.cs`.

### A3: Split the world repository

**Owns:**
- The six `Repositories/Sqlite*Repository.cs` main files. Their bodies are the existing SQL, moved without change.
- `SqliteWorldPersistenceRepository.cs`, rewritten as a facade of about 20 one-line forwards. It keeps the same class name and the `(SqliteSchemaBootstrapper)` constructor, plus `internal const string LoadPlayerInventorySql = SqliteInventoryRepository.LoadPlayerInventorySql;`.
- `dream.Tests/Server/Persistence/Repositories/*Tests.cs`.

**Rules:**
- `LoadPlayerInventory` stays a single statement, so the query-plan test still applies.
- `ReassignEnemyId` stays a single `UPDATE`.
- Every call uses one session through `Store.OpenSession()`, so it enlists.

**Tests:**
- Each repository's create, read, update and delete round-trip.
- Each repository's writes inside a batch enlist (one commit).
- The facade and the parts read back identical rows.

**Must not touch:** the `*.Checkpoint.cs` partials, `IWorldPersistenceRepository.cs`, the checkpoint writer, Simulation.

### A4: Checkpoint writer on the repositories, and the guard

**Owns:**
- `SqlitePersistenceCheckpointWriter.cs`. It keeps its constructor, uses `store.BeginOwnedTransaction()`, and holds no SQL.
- New partial files, each with `internal static void Checkpoint…(SqliteSession s, IReadOnlyList<…> rows)` methods. Each method prepares one command per table and reuses its parameters, as today:
  - `Repositories/SqlitePlayerWorldStateRepository.Checkpoint.cs`: `CheckpointUpsert`, guarded.
  - `Repositories/SqlitePlacedObjectRepository.Checkpoint.cs`: `CheckpointUpdate`.
  - `Repositories/SqliteRubbleRepository.Checkpoint.cs`: `CheckpointUpdate`.
  - `Repositories/SqliteEnemyRepository.Checkpoint.cs`: `CheckpointUpdate`.
  - `SqlitePlayerProfileRepository.Checkpoint.cs`: `CheckpointUpdateVitals`, guarded.
  - `SqliteWorldMetadataRepository.Checkpoint.cs`: `WriteWorldTime`.
- In Simulation:
  - `PersistenceCheckpoint.cs` (`CapturedUtc`);
  - the `CaptureConnectedPlayers` part of `RuntimePersistenceBridge.Checkpoints.cs`;
  - `RepositoryPersistenceCheckpointWriter.cs`;
  - the comment in `IPersistenceCheckpointWriter.cs`.
- `dream.Tests/Server/Persistence/CheckpointLeaveRaceGuardTests.cs`.

**Tests:**
1. Capture, then the player leaves (the queue writes the world state and the profile), then the stale checkpoint is written. The values from the leave survive, for both the world-state row and the vitals.
2. A checkpoint captured after a rejoin does overwrite.
3. The first insert, with no existing row, still inserts.
4. Merged checkpoints keep each row's time.
5. Vitals with the default `CapturedUtc` behave as before.
6. The fallback writer behaves the same in both cases.
7. A mixed-format `updated_utc` (a legacy value with 3 fractional digits) behaves correctly.

**Must not touch:** the repositories' main files, the facade, the bootstrapper, the store.

### A5: Safety net, rules and docs (merges first)

**Owns:**
- `dream.Tests/Server/Persistence/WorldRoundTripCharacterizationTests.cs`. It writes through every facade method, the profile and the metadata, reopens the database through a fresh bootstrapper, and compares field by field, including the `updated_utc` formats. It passes on today's code.
- `dream.Tests/ProjectStructure/DependencyDirectionTests.cs`: add `"Microsoft.Data.Sqlite"` to `SimulationForbiddenTokens`.
- Docs:
  - `docs/server-persistence-ddl.md`: the migrations are the source of truth; `schema_version`; backups; the guard; the formula-seed note.
  - The persistence section of `docs/architecture.md`.
- It records baseline numbers at C0, before any merge: `PersistenceThroughputMeasurementTests`, `IncrementalCheckpointMeasurementTests` and the join-prefetch timings. They go in a draft file, `docs/ledger/work-log/2026-10-28-persistence-wave-a-baseline.md`.

**Must not touch:** production code.

## Acceptance

These apply to every workstream:

- `dotnet build` passes. All existing persistence and integration tests pass **unmodified**, including:
  - `PersistenceGroupCommitTests` and `Wave10GroupCommitRegistrationTests` (the write queue still enlists, with one commit per group);
  - `IncrementalCheckpointTests` (an idle world gives a null capture and 0 bytes written, and the writer opens nothing for an empty checkpoint);
  - the Wave8 and Wave9 tests, `WorldClockPersistenceTests`, and `PerPlayerInventoryPersistenceTests` including its query-plan test.
- No schema semantics change (A1's snapshot equality).
- No behaviour change apart from the A4 guard and the documented formula-seed change.
- Checkpoint write time and the join prefetch stay within 10% of A5's baseline. No per-row closures or new allocations on the checkpoint path.
- No file that wave T2 owns is touched. `git diff --stat` stays inside `Server/Persistence`, `Simulation/Persistence`, `config/` (`.sql` files and `migrations/`), the one bootstrap line, tests and docs.

## Merge Order And Glue

The merge order is C0, A5, A1, A2, A3, A4. Build and test after each merge.

After the last merge, a glue commit:
- confirms that no `NotImplementedException` is left in `Repositories/`;
- re-runs the measurement tests against the baseline;
- adds the wave-A work-log.

Risks:
- Every part of a partial class must carry `partial`; C0 sets this.
- The baseline can drift from `init.sql`; A1's snapshot test catches this.
