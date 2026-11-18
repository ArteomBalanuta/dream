using Dream.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Dream.Tests;

// The ONLY place in the test assembly that is allowed to touch Microsoft.Data.Sqlite's connection
// pools. SqliteConnection.ClearAllPools is process-wide: with these tests running concurrently it
// would destroy the idle connections of every neighbour, and closing the last connection of a WAL
// database checkpoints and truncates its -wal file, so one test's teardown reshapes a concurrent
// neighbour's database under it. ClearPool clears only the pool of one connection string, which is
// per test (every test database lives in a GUID temp directory).
//
// NoProcessWidePoolClearGuardTests pins that rule: it fails if ClearAllPools appears anywhere in
// dream.Tests outside this file.
internal static class SqlitePools
{
    // Releases the pooled connections of one test's own database, so the file is closed before a
    // restart or a raw read.
    public static void Release(SqliteSchemaBootstrapper schema)
    {
        Clear(schema.ConnectionString);
    }

    public static void Release(SqliteSchemaBootstrapper first, SqliteSchemaBootstrapper second)
    {
        Release(first);
        Release(second);
    }

    // By file path, for the call sites that hold a path rather than the bootstrapper that made it.
    //
    // A pool is keyed by the exact connection string, so every string that this repo ever opens the
    // file under has to be cleared. There are only two: the bootstrapper's
    // (Data Source=<path>;Default Timeout=5, the one the server, the write queue and every
    // repository use) and the bare Data Source=<path> that a couple of test fixtures open for raw
    // reads. Everything else in the repo that opens a database file by hand sets Pooling=False, so
    // it is never in a pool at all. Clearing a string that has no pool is a no-op, so listing both
    // is cheap and keeps this correct whichever side opened the file last.
    public static void Release(string databasePath)
    {
        Clear(new SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 5 }.ToString());
        Clear(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
    }

    public static void Release(string firstDatabasePath, string secondDatabasePath)
    {
        Release(firstDatabasePath);
        Release(secondDatabasePath);
    }

    private static void Clear(string connectionString)
    {
        using SqliteConnection connection = new(connectionString);
        SqliteConnection.ClearPool(connection);
    }
}
