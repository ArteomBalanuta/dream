using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Migrations;

// Migration 0001. Its script is the pre-migration schema (player-profiles.init.sql plus
// world_metadata), written idempotently, so it serves both a new database and every database a
// pre-migration server created. Apply repeats the old bootstrapper's sequence exactly: the script,
// world_metadata, then the four columns such a database may predate. This is the only migration that probes
// columns, and it runs only from version 0 to 1. Later migrations change the schema unconditionally.
public sealed class BaselineMigration : SqlScriptMigration
{
    public const int BaselineVersion = 1;

    private static readonly (string Table, string Column, string Alter)[] LegacyColumns =
    [
        ("player_profiles", "experience",
            "ALTER TABLE player_profiles ADD COLUMN experience INTEGER NOT NULL DEFAULT 0;"),
        ("placed_cube_instances", "center_x", "ALTER TABLE placed_cube_instances ADD COLUMN center_x REAL NULL;"),
        ("placed_cube_instances", "center_y", "ALTER TABLE placed_cube_instances ADD COLUMN center_y REAL NULL;"),
        ("placed_cube_instances", "center_z", "ALTER TABLE placed_cube_instances ADD COLUMN center_z REAL NULL;"),
    ];

    public BaselineMigration(string name, string script)
        : base(BaselineVersion, name, script)
    {
    }

    // Already in 0001_baseline.sql; repeated (idempotently) so a hand-written schema script used as the
    // baseline gets it too, as the pre-migration bootstrapper gave it.
    private const string WorldMetadataSql =
        "CREATE TABLE IF NOT EXISTS world_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);";

    public override void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        base.Apply(connection, transaction);
        using (SqliteCommand metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText = WorldMetadataSql;
            metadata.ExecuteNonQuery();
        }

        foreach ((string table, string column, string alter) in LegacyColumns)
        {
            if (ColumnExists(connection, transaction, table, column))
            {
                continue;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = alter;
            command.ExecuteNonQuery();
        }
    }

    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.Transaction = transaction;
        pragma.CommandText = $"PRAGMA table_info({table});";
        using SqliteDataReader reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
