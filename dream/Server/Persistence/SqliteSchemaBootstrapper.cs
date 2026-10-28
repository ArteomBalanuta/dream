using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

public sealed class SqliteSchemaBootstrapper
{
    private readonly string _connectionString;
    private readonly string _schemaScriptPath;

    public SqliteSchemaBootstrapper(string databasePath, string schemaScriptPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
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

    public void EnsureSchema()
    {
        string script = File.ReadAllText(_schemaScriptPath);
        using SqliteConnection connection = CreateOpenConnection();
        using (SqliteCommand journal = connection.CreateCommand())
        {
            // Persistent: once set, every later connection to this file opens in WAL mode.
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            journal.ExecuteScalar();
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = script;
        command.ExecuteNonQuery();
        // Additive: databases created before the world_metadata table existed gain it here, and
        // SqliteWorldMetadataRepository fills in the simulation seed on their first load.
        using (SqliteCommand metadata = connection.CreateCommand())
        {
            metadata.CommandText =
                "CREATE TABLE IF NOT EXISTS world_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);";
            metadata.ExecuteNonQuery();
        }

        EnsureColumnExists(
            connection,
            tableName: "player_profiles",
            columnName: "experience",
            alterStatement: "ALTER TABLE player_profiles ADD COLUMN experience INTEGER NOT NULL DEFAULT 0;");
        EnsureColumnExists(
            connection,
            tableName: "placed_cube_instances",
            columnName: "center_x",
            alterStatement: "ALTER TABLE placed_cube_instances ADD COLUMN center_x REAL NULL;");
        EnsureColumnExists(
            connection,
            tableName: "placed_cube_instances",
            columnName: "center_y",
            alterStatement: "ALTER TABLE placed_cube_instances ADD COLUMN center_y REAL NULL;");
        EnsureColumnExists(
            connection,
            tableName: "placed_cube_instances",
            columnName: "center_z",
            alterStatement: "ALTER TABLE placed_cube_instances ADD COLUMN center_z REAL NULL;");
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

    // The connection a repository call runs on. Inside a SqliteWriteBatch (the write queue's worker
    // groups its writes into one transaction) it is the batch's open connection, which the lease
    // does not close; anywhere else it is a new connection the lease closes.
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

    private static void EnsureColumnExists(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string alterStatement)
    {
        bool found = false;
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({tableName});";
            using SqliteDataReader reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
        }

        if (found)
        {
            return;
        }

        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = alterStatement;
        alter.ExecuteNonQuery();
    }
}
