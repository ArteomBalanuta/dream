using Dream.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Dream.Tests.Server.Persistence.Migrations;

// A temp directory holding one database, plus the scripts the migration tests build legacy databases
// from. Plain connections (no pooling) so the file can be byte-compared and deleted.
internal sealed class MigrationTestDatabase : IDisposable
{
    public MigrationTestDatabase()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), $"dream-migration-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootDirectory);
        DatabasePath = Path.Combine(RootDirectory, "persistence.sqlite");
        Schema = new SqliteSchemaBootstrapper(DatabasePath, InitScriptPath);
    }

    public static string ConfigDirectory =>
        Path.Combine(Dream.Tests.Integration.RepoRoot.Find(), "dream", "config");

    // The frozen pre-migration schema script; the bootstrapper finds config/migrations next to it.
    public static string InitScriptPath => Path.Combine(ConfigDirectory, "player-profiles.init.sql");

    public static string MigrationsDirectory => Path.Combine(ConfigDirectory, "migrations");

    public static string PreCenterExperienceFixturePath => Path.Combine(
        Dream.Tests.Integration.RepoRoot.Find(), "dream.Tests", "Server", "Persistence", "Migrations", "Fixtures",
        "legacy-pre-center-experience.sql");

    public string RootDirectory { get; }

    public string DatabasePath { get; }

    public SqliteSchemaBootstrapper Schema { get; }

    public string[] Backups() => Directory.GetFiles(RootDirectory, "*.bak");

    // Exactly what SqliteSchemaBootstrapper.EnsureSchema did before migrations: WAL, the init
    // script, world_metadata, then the four probed columns. This is "today's schema".
    public void BuildWithPreMigrationBootstrapper()
    {
        using SqliteConnection connection = Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, File.ReadAllText(InitScriptPath));
        Execute(connection, "CREATE TABLE IF NOT EXISTS world_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);");
        AddColumnIfMissing(connection, "player_profiles", "experience", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "placed_cube_instances", "center_x", "REAL NULL");
        AddColumnIfMissing(connection, "placed_cube_instances", "center_y", "REAL NULL");
        AddColumnIfMissing(connection, "placed_cube_instances", "center_z", "REAL NULL");
    }

    public void ExecuteScript(string sql)
    {
        using SqliteConnection connection = Open();
        Execute(connection, sql);
    }

    public object? Scalar(string sql)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public SqliteConnection Open() => OpenFile(DatabasePath);

    public static SqliteConnection OpenFile(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // The bootstrapper's own connections are pooled; release them so the file is closed.
    public static void ReleasePooledConnections() => SqliteConnection.ClearAllPools();

    public void Dispose()
    {
        ReleasePooledConnections();
        try
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
    {
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({table});";
            using SqliteDataReader reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }
}
