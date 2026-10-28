using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Migrations;

// A migration that runs one SQL script (config/migrations/NNNN_name.sql) inside the migrator's
// transaction.
public class SqlScriptMigration : ISchemaMigration
{
    public SqlScriptMigration(int version, string name, string script)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(script);
        Version = version;
        Name = name;
        Script = script;
    }

    public int Version { get; }

    public string Name { get; }

    public string Script { get; }

    public virtual void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Script;
        command.ExecuteNonQuery();
    }
}
