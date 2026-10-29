using System.Globalization;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.World;
using Dream.Tests.Integration;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Server.Worlds;

// Tester, persistence wave B: the --worlds command line against a real legacy database (the v1 file
// of a developer's config folder, migrated to v3 by the bootstrap): create foo --size 20, list, copy 1
// bar, delete bar --yes, with every exit code checked, the seeds of all worlds absent from stdout and
// stderr, world 1 byte-identical after the copy and delete, and the database intact. The server is
// not running. A second test starts two hosts on the same unmigrated legacy file at once (two
// processes starting together after an upgrade) and expects a single migration.
public sealed class WaveBTesterCliTests(ITestOutputHelper output)
{
    [Fact]
    public void CreateListCopyDelete_OnARealMigratedDatabase_ExitCodesAndNoSeedLeak_AndWorld1Untouched()
    {
        using MigrationTestDatabase database = new();
        File.Copy(LegacyFixture("real-legacy-v1.db"), database.DatabasePath);
        using (ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath))
        {
        }

        SqliteConnection.ClearAllPools();
        string seedOne = (string)database.Scalar("SELECT seed FROM worlds WHERE world_id = 1;")!;
        long timeOne = Convert.ToInt64(database.Scalar("SELECT world_time_ticks FROM worlds WHERE world_id = 1;"), CultureInfo.InvariantCulture);
        string[] before = WorldBundles.WorldTables.Select(table => WorldBundles.Dump(database, table, WorldId.Default)).ToArray();
        Assert.Contains(before, dump => dump.Length > 0);

        SqliteWorldCatalog Open() => new(database.Schema);
        List<string> allText = [];

        (int exit, string created) = Run(["create", "foo", "--size", "20"]);
        Assert.Equal(0, exit);
        Assert.Equal(["2", "foo", "20x8x20", "default:1", "0"], created.TrimEnd().Split('\t'));

        (exit, string listed) = Run(["list"]);
        Assert.Equal(0, exit);
        string[] lines = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["1\tdefault\t56x8x56\tdefault:1\t" + timeOne, "2\tfoo\t20x8x20\tdefault:1\t0"], lines);

        (exit, string copied) = Run(["copy", "1", "bar"]);
        Assert.Equal(0, exit);
        Assert.Equal(["3", "bar", "56x8x56", "default:1", timeOne.ToString(CultureInfo.InvariantCulture)], copied.TrimEnd().Split('\t'));
        SqliteConnection.ClearAllPools();
        foreach (string table in WorldBundles.WorldTables)
        {
            Assert.Equal(WorldBundles.Count(database, table, WorldId.Default), WorldBundles.Count(database, table, new WorldId(3)));
        }

        Assert.Equal(seedOne, database.Scalar("SELECT seed FROM worlds WHERE world_id = 3;"));

        (exit, string deleted) = Run(["delete", "bar", "--yes"]);
        Assert.Equal(0, exit);
        Assert.StartsWith("deleted\t3\tbar", deleted);

        Assert.Equal(2, Run(["delete", "bar", "--yes"]).Exit);
        Assert.Equal(1, Run(["delete", "foo"]).Exit);
        Assert.Equal(2, Run(["create", "FOO"]).Exit);
        Assert.Equal(2, Run(["copy", "1", "Foo"]).Exit);
        Assert.Equal(2, Run(["copy", "77", "x"]).Exit);
        Assert.Equal(1, Run(["create", "big", "--size", "101"]).Exit);
        Assert.Equal(1, Run(["create", "small", "--size", "4"]).Exit);
        Assert.Equal(1, Run(["create", new string('n', 65)]).Exit);
        Assert.Equal(1, Run(["create", "   "]).Exit);
        Assert.Equal(1, Run([]).Exit);
        Assert.Equal(1, Run(["list", "--yes"]).Exit);

        SqliteConnection.ClearAllPools();
        string seedFoo = (string)database.Scalar("SELECT seed FROM worlds WHERE world_id = 2;")!;
        Assert.NotEqual(seedOne, seedFoo);
        string everything = string.Join("\n", allText);
        Assert.DoesNotContain(seedOne, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(seedFoo, everything, StringComparison.Ordinal);

        // World 1 is exactly what the migration left; bar is gone completely; foo is untouched.
        Assert.Equal(before, WorldBundles.WorldTables.Select(table => WorldBundles.Dump(database, table, WorldId.Default)).ToArray());
        foreach (string table in WorldBundles.WorldTables)
        {
            Assert.Equal(0L, WorldBundles.Count(database, table, new WorldId(3)));
            Assert.Equal(0L, WorldBundles.Count(database, table, new WorldId(2)));
        }

        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM worlds;"));
        Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));
        using (SqliteConnection connection = database.Open())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.False(reader.Read());
        }

        // Ids are never reused: the next world is 4, not 3.
        (exit, string next) = Run(["create", "again"]);
        Assert.Equal(0, exit);
        Assert.StartsWith("4\tagain\t", next);
        output.WriteLine(everything);

        (int Exit, string Out) Run(string[] args)
        {
            StringWriter stdout = new();
            StringWriter stderr = new();
            int code = WorldCli.Run(args, Open, stdout, stderr);
            allText.Add(stdout.ToString());
            allText.Add(stderr.ToString());
            return (code, stdout.ToString());
        }
    }

    [Fact]
    public async Task TwoHostsStartingTogetherOnAnUnmigratedLegacyFile_MigrateOnce_AndBothOpenWorld1()
    {
        using MigrationTestDatabase database = new();
        File.Copy(LegacyFixture("real-legacy-v1.db"), database.DatabasePath);
        using ManualResetEventSlim go = new();

        ServerPersistenceBundle Start() =>
            ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath);

        Task<ServerPersistenceBundle> a = Task.Run(() =>
        {
            go.Wait();
            return Start();
        });
        Task<ServerPersistenceBundle> b = Task.Run(() =>
        {
            go.Wait();
            return Start();
        });
        go.Set();
        using ServerPersistenceBundle first = await a;
        using ServerPersistenceBundle second = await b;

        Assert.Equal(WorldId.Default, first.WorldId);
        Assert.Equal(first.SimulationSeed, second.SimulationSeed);
        Assert.Equal(first.WorldTimeTicks, second.WorldTimeTicks);
        SqliteConnection.ClearAllPools();
        Assert.Equal(3L, database.Scalar("SELECT MAX(version) FROM schema_version;"));
        Assert.Equal(3L, database.Scalar("SELECT COUNT(*) FROM schema_version;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM worlds;"));
        Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));
        Assert.Equal(5L, WorldBundles.Count(database, "placed_cube_instances", WorldId.Default));
    }

    // Whatever the losing starter does, the file must come out as one clean v3 database that the next start opens.
    [Fact]
    public async Task TwoHostsStartingTogetherOnAnUnmigratedLegacyFile_LeaveACleanV3Database_EvenIfOneStarterFails()
    {
        using MigrationTestDatabase database = new();
        File.Copy(LegacyFixture("real-legacy-v1.db"), database.DatabasePath);
        using ManualResetEventSlim go = new();
        Task<ServerPersistenceBundle> Start() => Task.Run(() =>
        {
            go.Wait();
            return ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath);
        });
        Task<ServerPersistenceBundle>[] starters = [Start(), Start()];
        go.Set();
        await Task.WhenAny(Task.WhenAll(starters).ContinueWith(_ => 0));
        foreach (Task<ServerPersistenceBundle> starter in starters)
        {
            if (starter.IsCompletedSuccessfully)
            {
                starter.Result.Dispose();
            }
            else
            {
                output.WriteLine("starter failed: " + starter.Exception?.GetBaseException().Message);
            }
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));
        Assert.Equal(3L, database.Scalar("SELECT MAX(version) FROM schema_version;"));
        Assert.Equal(3L, database.Scalar("SELECT COUNT(*) FROM schema_version;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM worlds;"));
        Assert.Equal(5L, WorldBundles.Count(database, "placed_cube_instances", WorldId.Default));
        Assert.Equal(12L, WorldBundles.Count(database, "player_inventory_slots", WorldId.Default));
        using ServerPersistenceBundle again = ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, MigrationTestDatabase.InitScriptPath);
        Assert.Equal(WorldId.Default, again.WorldId);
    }

    private static string LegacyFixture(string name) => Path.Combine(
        RepoRoot.Find(), "dream.Tests", "Server", "Persistence", "Migrations", "Fixtures", name);
}
