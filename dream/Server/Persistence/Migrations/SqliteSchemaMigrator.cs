using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Migrations;

// Brings a database up to the newest migration. The applied versions are recorded in schema_version,
// one row per migration. Every pending migration runs in ONE transaction: a failure rolls all of them
// back and the database stays at its old version.
public sealed class SqliteSchemaMigrator : ISchemaMigrator
{
    private const string CreateVersionTableSql =
        "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL PRIMARY KEY, name TEXT NOT NULL, applied_utc TEXT NOT NULL);";

    // The stats-sheet line shipped its own migration 11 (character_stat_points: player_profiles.stat_points)
    // before the multiplayer line's 0011..0014 were merged in. A database that ran it records version 11
    // under that name and has no enemy_instances.camp_key (0011_spawn_camps). Such a file is repaired
    // inside the migration transaction: the column is dropped, the row deleted, and 0011..0014 run from 10.
    internal const int DivergentVersion = 11;
    internal const string DivergentName = "character_stat_points";

    private readonly IReadOnlyList<ISchemaMigration> _migrations;

    public SqliteSchemaMigrator(IReadOnlyList<ISchemaMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        SchemaMigrations.Validate(migrations);
        _migrations = migrations;
    }

    public int LatestVersion => _migrations.Count;

    public int GetCurrentVersion(SqliteConnection connection)
    {
        using SqliteCommand exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_version';";
        if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            return 0;
        }

        using SqliteCommand max = connection.CreateCommand();
        max.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(max.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // Concurrent first starts: the version is read once without a lock (the common, already-migrated
    // case stays cheap), and again after BeginTransaction() has taken the write lock. BeginTransaction()
    // is IMMEDIATE, so a second process waits (busy_timeout) for the first to commit, then sees the new
    // version and does nothing. The backup is taken under that lock, after the re-read confirmed a
    // migration is still needed. SQLite's backup API cannot read through the connection that holds the
    // write transaction ("database is locked"), so Backup copies through a separate reader; readers do
    // not conflict with the write lock, and no other process can change the file while it is held, so
    // the copy is exactly the pre-migration state.
    public SchemaMigrationResult Migrate(string databasePath, SqliteConnection connection, Action<string>? log = null)
    {
        int to = LatestVersion;
        int from = GetCurrentVersion(connection);
        if (from > to)
        {
            throw new SchemaTooNewException(from, to);
        }

        if (from == to)
        {
            return new SchemaMigrationResult(from, to, null);
        }

        // PRAGMA foreign_keys is a no-op inside a transaction, and with foreign keys on, DROP TABLE of a
        // parent runs an implicit DELETE that fires ON DELETE CASCADE (a table rebuild would wipe its
        // children). So the pragma is switched off here, outside the transaction, and restored in the
        // finally (a pooled connection keeps it). Integrity is checked before the commit instead.
        bool foreignKeysWereOn = GetForeignKeys(connection);
        if (foreignKeysWereOn)
        {
            SetForeignKeys(connection, false);
        }

        string? backupPath = null;
        try
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            from = GetCurrentVersion(connection);
            if (from > to)
            {
                throw new SchemaTooNewException(from, to);
            }

            bool divergent = IsDivergentStatPointsDatabase(connection, transaction);
            if (from == to && !divergent)
            {
                // Another process migrated while this one waited for the lock.
                return new SchemaMigrationResult(from, to, null);
            }

            // A new database has nothing to lose; any other gets a copy it can be restored from.
            if (HasUserTables(connection))
            {
                backupPath = Backup(databasePath, connection, from, to);
                log?.Invoke($"Database backed up to {backupPath} before the schema migration.");
            }

            int first = from;
            if (divergent)
            {
                Execute(connection, transaction, "ALTER TABLE player_profiles DROP COLUMN stat_points;");
                Execute(connection, transaction, $"DELETE FROM schema_version WHERE version = {DivergentVersion};");
                first = DivergentVersion - 1;
                log?.Invoke(
                    $"Database ran the retired migration {DivergentVersion:D4} ('{DivergentName}'): dropped player_profiles.stat_points " +
                    $"(points are derived from the level now) and running the migrations from version {first}.");
            }

            Execute(connection, transaction, CreateVersionTableSql);
            for (int i = first; i < to; i++)
            {
                ISchemaMigration migration = _migrations[i];
                migration.Apply(connection, transaction);
                ThrowIfTransactionEnded(connection, transaction, migration);
                using SqliteCommand record = connection.CreateCommand();
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT INTO schema_version (version, name, applied_utc) VALUES ($version, $name, $appliedUtc);";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue("$appliedUtc", SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow));
                record.ExecuteNonQuery();
            }

            ThrowIfForeignKeyViolations(connection, transaction);
            transaction.Commit();
        }
        finally
        {
            if (foreignKeysWereOn)
            {
                SetForeignKeys(connection, true);
            }
        }

        log?.Invoke(from == 0 && backupPath == null
            ? $"Database schema created at version {to}."
            : $"Database schema migrated from version {from} to {to}.");
        return new SchemaMigrationResult(from, to, backupPath);
    }

    // True for a file that recorded the stats-sheet line's migration 11 (see DivergentVersion).
    private static bool IsDivergentStatPointsDatabase(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_version') > 0 " +
            "AND EXISTS (SELECT 1 FROM pragma_table_info('player_profiles') WHERE name = 'stat_points') " +
            "AND NOT EXISTS (SELECT 1 FROM pragma_table_info('enemy_instances') WHERE name = 'camp_key');";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            return false;
        }

        using SqliteCommand row = connection.CreateCommand();
        row.Transaction = transaction;
        row.CommandText = "SELECT COUNT(*) FROM schema_version WHERE version = $version AND name = $name;";
        row.Parameters.AddWithValue("$version", DivergentVersion);
        row.Parameters.AddWithValue("$name", DivergentName);
        return Convert.ToInt64(row.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool GetForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void SetForeignKeys(SqliteConnection connection, bool on)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = on ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;";
        command.ExecuteNonQuery();
    }

    // Foreign keys were off while the migrations ran, so an orphan row (for example an item whose
    // world_id has no worlds row, or a slot whose item is gone) is only found here. Throwing rolls
    // everything back; the backup taken before the migration still exists.
    private static void ThrowIfForeignKeyViolations(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = check.ExecuteReader();
        List<string> violations = [];
        while (reader.Read())
        {
            if (violations.Count == 10)
            {
                violations.Add("...");
                break;
            }

            string rowId = reader.IsDBNull(1) ? "(none)" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!;
            violations.Add($"table '{reader.GetString(0)}' rowid {rowId} references missing parent '{reader.GetString(2)}'");
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "Schema migration failed its foreign key check and was rolled back; the database stays at its old version: " +
                string.Join("; ", violations) + ".");
        }
    }

    // SchemaMigrations.LoadFromDirectory rejects transaction control only at the start of a line, so
    // a script such as "...; COMMIT;" on one line (or a hand-written ISchemaMigration) can still end
    // the migrator's transaction. What ran before that point is then committed and cannot be undone;
    // the remaining migrations must not run in autocommit, and no version is recorded for this one.
    // The backup taken before the transaction is the way back. SQLite's autocommit flag is set
    // exactly when no transaction is open.
    private static void ThrowIfTransactionEnded(SqliteConnection connection, SqliteTransaction transaction, ISchemaMigration migration)
    {
        if (SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) == 0)
        {
            return;
        }

        // After a COMMIT, SqliteTransaction.Dispose still issues ROLLBACK, which would fail with "no
        // transaction is active" and hide this error; an empty transaction gives it something to roll
        // back. After a ROLLBACK the transaction knows it is over and refuses the command; Dispose
        // then issues nothing.
        try
        {
            Execute(connection, transaction, "BEGIN;");
        }
        catch (InvalidOperationException)
        {
        }

        throw new InvalidOperationException(
            $"Schema migration {migration.Version:D4} ('{migration.Name}'): migration {migration.Version:D4} ended the transaction. " +
            "Migration scripts must not COMMIT, ROLLBACK or END TRANSACTION; whatever it ran before that point is committed, " +
            "so restore the backup taken before the migration if the database is now inconsistent.");
    }

    private static bool HasUserTables(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\';";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    // {db}.v{from:D4}-to-v{to:D4}.{yyyyMMddTHHmmssZ}.bak, written with the SQLite online backup API, so
    // it is a consistent copy even with a WAL file next to the database.
    private static string Backup(string databasePath, SqliteConnection connection, int from, int to)
    {
        if (string.IsNullOrEmpty(databasePath) || databasePath == ":memory:")
        {
            throw new InvalidOperationException("A database with existing tables needs a file path to be backed up before a schema migration.");
        }

        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string backupPath = $"{databasePath}.v{from:D4}-to-v{to:D4}.{stamp}.bak";
        for (int attempt = 1; File.Exists(backupPath); attempt++)
        {
            backupPath = $"{databasePath}.v{from:D4}-to-v{to:D4}.{stamp}-{attempt}.bak";
        }

        try
        {
            // A second, read-only-in-effect connection is the backup source: SQLite's backup API reports
            // "database is locked" when its source connection holds the write transaction. A reader
            // does not conflict with the held write lock and sees the last committed state, which is
            // the pre-migration state because the transaction has written nothing yet.
            using SqliteConnection source = new(new SqliteConnectionStringBuilder(connection.ConnectionString)
            {
                Pooling = false,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            source.Open();
            using SqliteConnection destination = new(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Pooling = false
            }.ToString());
            destination.Open();
            source.BackupDatabase(destination);
        }
        catch
        {
            TryDelete(backupPath);
            throw;
        }

        return backupPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
