using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Migrations;

// One step of the schema history, loaded from config/migrations/NNNN_snake_case.sql.
public interface ISchemaMigration
{
    // 1-based and contiguous across the migration set.
    int Version { get; }

    // From the file name (the snake_case part).
    string Name { get; }

    // Runs inside the migrator's single transaction; never commits it.
    void Apply(SqliteConnection connection, SqliteTransaction transaction);
}
