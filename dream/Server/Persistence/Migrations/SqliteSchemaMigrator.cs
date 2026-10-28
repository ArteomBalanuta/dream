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

    public SchemaMigrationResult Migrate(string databasePath, SqliteConnection connection, Action<string>? log = null)
    {
        int from = GetCurrentVersion(connection);
        int to = LatestVersion;
        if (from > to)
        {
            throw new SchemaTooNewException(from, to);
        }

        if (from == to)
        {
            return new SchemaMigrationResult(from, to, null);
        }

        // A new database has nothing to lose; any other gets a copy it can be restored from.
        string? backupPath = HasUserTables(connection) ? Backup(databasePath, connection, from, to) : null;
        if (backupPath != null)
        {
            log?.Invoke($"Database backed up to {backupPath} before the schema migration.");
        }

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Execute(connection, transaction, CreateVersionTableSql);
            for (int i = from; i < to; i++)
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

            transaction.Commit();
        }

        log?.Invoke(from == 0 && backupPath == null
            ? $"Database schema created at version {to}."
            : $"Database schema migrated from version {from} to {to}.");
        return new SchemaMigrationResult(from, to, backupPath);
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
            using SqliteConnection destination = new(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Pooling = false
            }.ToString());
            destination.Open();
            connection.BackupDatabase(destination);
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
