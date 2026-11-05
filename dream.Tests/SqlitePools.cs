using Dream.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Dream.Tests;

internal static class SqlitePools
{
    // Releases the pooled connections of one test's own database, so the file is closed before a
    // restart or a raw read. SqliteConnection.ClearAllPools is process-wide: with these tests
    // running concurrently it would destroy the idle connections of every neighbour, and closing
    // the last connection of a WAL database checkpoints and truncates its -wal file.
    // ClearPool clears only the pool of this connection string, which is per test (a GUID temp dir).
    public static void Release(SqliteSchemaBootstrapper schema)
    {
        using SqliteConnection connection = new(schema.ConnectionString);
        SqliteConnection.ClearPool(connection);
    }
}
