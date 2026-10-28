using System.Globalization;
using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

public sealed class SqliteSchemaBootstrapper
{
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly string _schemaScriptPath;
    private readonly bool _schemaScriptIsBaselineWithoutMigrations;

    public SqliteSchemaBootstrapper(string databasePath, string schemaScriptPath)
        : this(databasePath, schemaScriptPath, schemaScriptIsBaselineWithoutMigrations: false)
    {
    }

    // Tests only: schemaScriptIsBaselineWithoutMigrations lets a schema script with no migrations
    // directory next to it (a test's hand-written schema in a temp folder) be its own single baseline
    // migration. Production never sets it, so a missing directory fails startup.
    internal SqliteSchemaBootstrapper(string databasePath, string schemaScriptPath, bool schemaScriptIsBaselineWithoutMigrations)
    {
        _schemaScriptIsBaselineWithoutMigrations = schemaScriptIsBaselineWithoutMigrations;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            // Microsoft.Data.Sqlite retries a busy statement until the command timeout (30 s by
            // default) on top of busy_timeout, so a locked database held each write for ~33 s.
            // Matching busy_timeout bounds a write's wait for the lock at ~5 s.
            DefaultTimeout = 5
        }.ToString();
        _schemaScriptPath = schemaScriptPath;
    }

    // Per-connection settings. journal_mode=WAL is stored in the database file (EnsureSchema sets
    // it once); synchronous and busy_timeout are per connection, so every connection sets them.
    // WAL with synchronous=NORMAL syncs at checkpoints instead of on every commit, and readers no
    // longer block the writer. busy_timeout makes a writer wait for another connection's write
    // (the checkpoint writer and the write queue run on different threads) instead of failing.
    // Durability trade-off: in WAL mode synchronous=NORMAL never corrupts the database, but a power
    // loss or OS crash (not a process crash) can lose the last few commits before the next WAL
    // sync, which is accepted here for game state.
    // journal_size_limit truncates the -wal file back to 64 MB after a checkpoint, so one large
    // burst (a fracture storm) does not leave a huge WAL file on disk for the rest of the run.
    private const string ConnectionPragmas =
        "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA journal_size_limit=67108864;";

    public string ConnectionString => _connectionString;

    // The migrations directory sits next to the schema script: config/migrations/NNNN_name.sql.
    public string MigrationsDirectory =>
        Path.Combine(Path.GetDirectoryName(_schemaScriptPath) ?? ".", "migrations");

    // Puts the database in WAL mode, then applies the pending migrations from MigrationsDirectory.
    // Throws SchemaTooNewException, before anything is written, when a newer build already migrated
    // the file, and DirectoryNotFoundException, before the database is opened, when
    // MigrationsDirectory is missing (a broken install).
    public SchemaMigrationResult EnsureSchema(Action<string>? log = null)
    {
        return EnsureSchema(new SqliteSchemaMigrator(LoadMigrations()), log);
    }

    // The shipped config/player-profiles.init.sql always has config/migrations next to it; without
    // it the schema is unknown, so startup fails rather than guessing. Only the test constructor's
    // flag lets a schema script without that directory be its own single baseline migration, which
    // reproduces what the pre-migration bootstrapper did with it.
    private IReadOnlyList<ISchemaMigration> LoadMigrations()
    {
        string directory = MigrationsDirectory;
        if (!Directory.Exists(directory) && _schemaScriptIsBaselineWithoutMigrations)
        {
            return [new BaselineMigration("baseline", File.ReadAllText(_schemaScriptPath))];
        }

        // Throws DirectoryNotFoundException for a missing directory.
        return SchemaMigrations.LoadFromDirectory(directory);
    }

    public SchemaMigrationResult EnsureSchema(ISchemaMigrator migrator, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(migrator);
        using SqliteConnection connection = CreateOpenConnection();
        // Checked before the journal-mode change, which would otherwise rewrite the file header of a
        // database this build must leave untouched.
        int stored = migrator.GetCurrentVersion(connection);
        if (stored > migrator.LatestVersion)
        {
            throw new SchemaTooNewException(stored, migrator.LatestVersion);
        }

        using (SqliteCommand journal = connection.CreateCommand())
        {
            // Persistent: once set, every later connection to this file opens in WAL mode. It cannot
            // change inside a transaction, so it runs before the migrator's.
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            journal.ExecuteScalar();
        }

        return migrator.Migrate(_databasePath, connection, log);
    }

    public SqliteConnection CreateOpenConnection()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand pragmas = connection.CreateCommand();
        pragmas.CommandText = ConnectionPragmas;
        pragmas.ExecuteNonQuery();
        return connection;
    }

    // A connection with SqliteWorldStore.OpenSession's enlistment rule, for tests and tools that
    // need a raw connection: inside a SqliteWriteBatch (the write queue's worker groups its writes
    // into one transaction) it is the batch's open connection, which the lease does not close;
    // anywhere else it is a new connection the lease closes. The repositories no longer use it; they
    // open sessions through IWorldStore.
    public SqliteConnectionLease OpenConnection()
    {
        SqliteConnection? enlisted = SqliteWriteBatch.TryEnlist(this);
        return enlisted != null
            ? new SqliteConnectionLease(enlisted, owned: false)
            : new SqliteConnectionLease(CreateOpenConnection(), owned: true);
    }

    public static string FormatUtc(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    public static DateTime ParseUtc(string value)
    {
        return DateTime.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}
