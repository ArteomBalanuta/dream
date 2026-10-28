using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Migrations;

public interface ISchemaMigrator
{
    int LatestVersion { get; }

    // 0 when schema_version is absent.
    int GetCurrentVersion(SqliteConnection connection);

    // Applies pending migrations in ONE transaction. Throws SchemaTooNewException when the stored version
    // > LatestVersion (file untouched). Backs the file up first when migrations are pending AND the
    // database already had user tables. Returns what it did.
    SchemaMigrationResult Migrate(string databasePath, SqliteConnection connection, Action<string>? log = null);
}
