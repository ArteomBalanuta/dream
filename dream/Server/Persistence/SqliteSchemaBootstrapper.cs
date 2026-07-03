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
}
