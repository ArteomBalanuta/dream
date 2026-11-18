using System.Diagnostics;
using Dream.Server.Persistence.Migrations;
using Dream.Tests.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Load;

// Wave H gate: the migrations from 0008 (item_instances.rolled_modifiers) onwards, on a v7 database
// holding 100,000 item rows, take at most one second and leave every row. No migration in the set
// rewrites item_instances. The target version is read from the migration set rather than pinned, so
// the row-preservation half of the gate keeps running as migrations are added.
public sealed class ItemRollsMigrationMeasurementTests(ITestOutputHelper output)
{
    private const int Items = 100_000;

    [LoadFact]
    public void Migration0008_On100000Items_TakesAtMostASecond_AndKeepsEveryRow()
    {
        using MigrationTestDatabase database = new();
        var all = SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory);
        database.Schema.EnsureSchema(new SqliteSchemaMigrator([.. all.Take(4)]));
        database.ExecuteScript(File.ReadAllText(Path.Combine(
            Dream.Tests.Integration.RepoRoot.Find(), "dream.Tests", "Server", "Persistence", "Migrations", "Fixtures",
            "v4-with-data.sql")));
        database.Schema.EnsureSchema(new SqliteSchemaMigrator([.. all.Take(7)]));

        using (SqliteConnection c = database.Open())
        {
            using SqliteCommand insert = c.CreateCommand();
            insert.CommandText = $"""
                WITH RECURSIVE n(i) AS (SELECT 1000 UNION ALL SELECT i + 1 FROM n WHERE i < {1000 + Items - 1})
                INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, owner_player_name, state)
                SELECT 1, i, 'tool.pick', 'Pick', 'pick', 'alice', 'Inventory' FROM n;
                """;
            insert.ExecuteNonQuery();
        }

        long before = Count(database);
        Stopwatch watch = Stopwatch.StartNew();
        SchemaMigrationResult result = database.Schema.EnsureSchema();
        watch.Stop();

        output.WriteLine($"migrations 0008..{all.Count:0000} on {before:N0} items: {watch.Elapsed.TotalMilliseconds:F1} ms (v{result.FromVersion} to v{result.ToVersion})");
        Assert.Equal(7, result.FromVersion);
        // The whole remaining set, not a frozen version: the hard-coded 10 went stale as migrations were
        // added, and the row-preservation assertion below never ran because this one failed first.
        Assert.Equal(all.Count, result.ToVersion);
        Assert.True(before >= Items, $"seeded {before}");
        Assert.Equal(before, Count(database));
        Assert.True(watch.Elapsed.TotalMilliseconds <= 1000, $"migration took {watch.Elapsed.TotalMilliseconds:F1} ms (gate 1000 ms)");
    }

    private static long Count(MigrationTestDatabase database)
    {
        using SqliteConnection c = database.Open();
        using SqliteCommand count = c.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM item_instances;";
        return (long)count.ExecuteScalar()!;
    }
}
