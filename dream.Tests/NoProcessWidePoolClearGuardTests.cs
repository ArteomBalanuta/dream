using Dream.Tests.Integration;
using Xunit;

namespace Dream.Tests;

// The rule TimingSensitiveCollection states and SqlitePools implements, pinned so it cannot come
// back by copy-paste.
//
// SqliteConnection.ClearAllPools() is process-wide. The parallel phase runs ~90 database test
// classes at once, each on its own GUID temp database, and a process-wide clear closes the idle
// connections of all of them. Closing the last connection of a WAL database checkpoints and
// truncates its -wal file, so one test's teardown can reshape a concurrent neighbour's database
// between that neighbour's write and its read. Releasing one connection string's pool
// (SqlitePools.Release) does exactly what every call site actually needs - free this test's own
// file - without reaching into anyone else's.
public sealed class NoProcessWidePoolClearGuardTests
{
    // The two files allowed to name the API at all, and then only in prose: the policy file and
    // this guard, which has to spell the forbidden text out to look for it.
    private static readonly string[] PolicyFiles = ["SqlitePools.cs", "NoProcessWidePoolClearGuardTests.cs"];

    [Fact]
    public void NoTestCallsClearAllPools()
    {
        string testRoot = Path.Combine(RepoRoot.Find(), "dream.Tests");
        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(testRoot, file);
            if (relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                || PolicyFiles.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                // The call, not the word: the policy comments name it on purpose.
                if (lines[i].Contains("ClearAllPools(", StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}:{i + 1}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    // The targeted replacement really is targeted: clearing one database's pool leaves another
    // database's pooled connection open and usable. This is the property the 239 converted call
    // sites rely on, so it is asserted rather than assumed.
    [Fact]
    public void ReleasingOnePool_LeavesAnotherDatabasesPooledConnectionOpen()
    {
        using Server.Persistence.Migrations.MigrationTestDatabase mine = new();
        using Server.Persistence.Migrations.MigrationTestDatabase neighbour = new();
        mine.Schema.EnsureSchema();
        neighbour.Schema.EnsureSchema();

        // A pooled connection returned to each pool.
        using (Microsoft.Data.Sqlite.SqliteConnection open = neighbour.Schema.CreateOpenConnection())
        {
            Microsoft.Data.Sqlite.SqliteCommand seed = open.CreateCommand();
            seed.CommandText = "INSERT INTO player_profiles (player_name, display_name) VALUES ('n', 'n');";
            seed.ExecuteNonQuery();
            seed.Dispose();
        }

        using (Microsoft.Data.Sqlite.SqliteConnection open = mine.Schema.CreateOpenConnection())
        {
        }

        SqlitePools.Release(mine.DatabasePath);

        // The neighbour is untouched: its row is still readable and its WAL was not checkpointed
        // away under it by my teardown.
        using Microsoft.Data.Sqlite.SqliteConnection after = neighbour.Schema.CreateOpenConnection();
        using Microsoft.Data.Sqlite.SqliteCommand count = after.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM player_profiles WHERE player_name = 'n';";
        Assert.Equal(1L, Convert.ToInt64(count.ExecuteScalar()));
    }
}
