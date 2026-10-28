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
            DataSource = databasePath
        }.ToString();
        _schemaScriptPath = schemaScriptPath;
    }

    public string ConnectionString => _connectionString;

    public void EnsureSchema()
    {
        string script = File.ReadAllText(_schemaScriptPath);
        using SqliteConnection connection = CreateOpenConnection();
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
        return connection;
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
