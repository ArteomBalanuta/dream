using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Server.Persistence;
using Dream.Simulation.Characters;
using Dream.Simulation.Content;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Integration;
using Dream.Tests.Support;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Load;

// Persistence wave F3: a server stopped in the middle of a session, with its clients still
// connected, and the restart that follows. The stop goes through
// WorldSessionRuntime.DisposeWithoutFinalCheckpoint (no final checkpoint, as after a kill that
// still let the write queue drain), the restart goes through the production bootstrap, and the same
// session ids rejoin.
//
// Later waves add their data to this test in one place: append an entry to Invariants (and a field
// to RestartObservation if it needs a new read). Each entry is checked after the restart against
// what the database held right after the stop, and on the observation of the first run.
[Collection(TimingSensitiveCollection.Name)]
public sealed class RestartMidSessionMeasurementTests(ITestOutputHelper output)
{
    private static readonly string[] Sessions = ["restart-a", "restart-b", "restart-c"];

    // Name, check. before: the database right after the stop. after: the database after the restart
    // server joined the same sessions and stopped again.
    internal static readonly (string Name, Action<RestartObservation, RestartObservation> Check)[] Invariants =
    [
        ("object ids are unique and below the stored counter", (before, after) =>
        {
            foreach (RestartObservation o in new[] { before, after })
            {
                Assert.Equal(o.Ids.AllObjects.Count, o.Ids.AllObjects.Distinct().Count());
                Assert.All(o.Ids.AllObjects, id => Assert.True(id < o.Ids.NextObjectId, $"id {id} >= counter {o.Ids.NextObjectId}"));
            }

            Assert.True(after.Ids.NextObjectId >= before.Ids.NextObjectId);
        }),
        ("ids that survived the first run are not reissued", (before, after) =>
        {
            Assert.Empty(before.Ids.Items.Except(after.Ids.Items));
            Assert.Empty(before.Ids.Cubes.Except(after.Ids.Cubes));
        }),
        ("the database passes integrity_check and the load-time check has no fatal finding", (before, after) =>
        {
            Assert.Equal("ok", before.IntegrityCheck);
            Assert.Equal("ok", after.IntegrityCheck);
            Assert.False(before.Report.IsFatal);
            Assert.False(after.Report.IsFatal);
            Assert.False(after.Report.HasSoftFindings, string.Join(" | ", after.Report.ToLogLines(WorldDescriptor.Default)));
        }),
        ("every session's profile and starter items survive the restart", (before, after) =>
        {
            foreach (string session in Sessions)
            {
                string player = WorldSessionRuntime.ResolveJoinPlayerName(session, $"Name {session}");
                Assert.Contains(player, before.ItemsByPlayer.Keys);
                Assert.Equal(before.ItemsByPlayer[player], after.ItemsByPlayer[player]);
                Assert.Contains(player, after.Profiles);
            }
        }),
        ("items keep their kind, and every stored item and enemy kind is in the shipped content", (before, after) =>
        {
            foreach ((long item, string kind) in before.ItemKinds)
            {
                Assert.Equal(kind, after.ItemKinds[item]);
            }

            foreach (RestartObservation o in new[] { before, after })
            {
                Assert.NotEmpty(o.ItemKinds);
                Assert.All(o.ItemKinds.Values, kind => Assert.True(ContentCatalog.Shipped.TryGetItem(kind, out _), kind));
                Assert.All(o.EnemyKinds, kind => Assert.True(ContentCatalog.Shipped.TryGetEnemy(kind, out _), kind));
            }
        }),
        ("every session's character is owned, and the owners survive the restart unchanged", (before, after) =>
        {
            // Checked when the run's hosts had accounts wired (the SQLite account repository in the bundle).
            if (!after.AccountsWired)
            {
                return;
            }

            foreach (string session in Sessions)
            {
                string player = WorldSessionRuntime.ResolveJoinPlayerName(session, $"Name {session}");
                Assert.NotNull(before.OwnerByCharacter[player]);
                Assert.Equal(before.OwnerByCharacter[player], after.OwnerByCharacter[player]);
            }

            Assert.Equal(before.AccountCount, after.AccountCount);
            Assert.Equal(Sessions.Length, after.AccountCount);
            Assert.Equal(0, before.AccountsOverLimit);
            Assert.Equal(0, after.AccountsOverLimit);
        }),
    ];

    internal sealed record RestartObservation(
        ObjectIdTestSupport.PersistedIds Ids,
        string IntegrityCheck,
        WorldIntegrityReport Report,
        IReadOnlyDictionary<string, long[]> ItemsByPlayer,
        IReadOnlyCollection<string> Profiles,
        IReadOnlyDictionary<long, string> ItemKinds,
        IReadOnlyList<string> EnemyKinds,
        // Wave G: the owning account of each character (null: unowned), the number of accounts, how many
        // accounts own more than 5 characters, and whether the run's hosts had accounts wired at all
        // (a host built without a SQLite account repository leaves every owner null).
        IReadOnlyDictionary<string, long?> OwnerByCharacter,
        int AccountCount,
        int AccountsOverLimit,
        bool AccountsWired);

    [Fact]
    public async Task AServerStoppedMidSession_RestartsWithTheInvariantsIntact_AndTheRejoinedClientsMatch()
    {
        using MigrationTestDatabase db = new();

        // Run 1: three clients connected and moving; the stop leaves them connected.
        Stopwatch firstStart = Stopwatch.StartNew();
        DedicatedServerHost host1 = Bootstrap(db, out _);
        bool accountsWired = host1.Runtime!.HasAccounts;
        firstStart.Stop();
        Assert.True(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));
        WireLoopbackServer server1 = await WireLoopbackServer.StartAsync(host1);
        List<WireLoopbackClient> clients = [];
        foreach (string session in Sessions)
        {
            clients.Add(await server1.ConnectAndJoinAsync(session));
        }

        for (int tick = 0; tick < 40; tick++)
        {
            foreach (WireLoopbackClient client in clients)
            {
                await client.SendCommandAsync(PlayerInteractionMode.Combat, new PlayerMoveIntent(tick % 2 == 0 ? 1f : -1f, 0f), Vector3.UnitZ, attack: tick % 7 == 0);
            }

            await server1.TickAsync();
            Assert.True(await server1.FlushAllAsync());
        }

        // The stop: sockets dropped by the service, no final checkpoint, the host released.
        await server1.Service.DisposeAsync();
        server1.Runtime.DisposeWithoutFinalCheckpoint();
        server1.Host.Dispose();
        foreach (WireLoopbackClient client in clients)
        {
            await client.DisposeAsync();
        }

        Assert.False(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default), "the stop released the world");
        RestartObservation before = Observe(db, accountsWired);
        output.WriteLine($"after the stop: {before.Ids.AllObjects.Count} objects, {before.ItemsByPlayer.Count} players with items, {before.EnemyKinds.Count} enemies, counter {before.Ids.NextObjectId}");
        Assert.Equal(Sessions.Length, before.ItemsByPlayer.Count);

        // Run 2: restart and rejoin; each client must build the server's map.
        Stopwatch secondStart = Stopwatch.StartNew();
        DedicatedServerHost host2 = Bootstrap(db, out List<string> log2);
        secondStart.Stop();
        Assert.Contains(log2, l => l.Contains("integrity check", StringComparison.Ordinal));
        output.WriteLine($"server start (bootstrap + host): first {firstStart.Elapsed.TotalMilliseconds:0.0} ms, restart {secondStart.Elapsed.TotalMilliseconds:0.0} ms");
        await using (WireLoopbackServer server2 = await WireLoopbackServer.StartAsync(host2))
        {
            List<WireLoopbackClient> rejoined = [];
            foreach (string session in Sessions)
            {
                rejoined.Add(await server2.ConnectAndJoinAsync(session));
            }

            for (int tick = 0; tick < 5; tick++)
            {
                await server2.TickAsync();
                Assert.True(await server2.FlushAllAsync());
            }

            if (accountsWired)
            {
                // A client with another token asking for an owned character is refused, and the owner stays seated.
                JoinRejectedMessage refused = await server2.ConnectAndGetJoinRejectionAsync(
                    "restart-stranger", $"Name {Sessions[0]}", TestAccountTokens.For("someone else"));
                Assert.Equal(JoinRejectionReasons.CharacterOwned, refused.Reason);
                Assert.True(server2.Host.IsConnectionJoined(server2.ConnectionFor(rejoined[0]).ConnectionId));
            }

            ulong serverChecksum = WorldStateChecksum.Compute(server2.Runtime.Map);
            foreach (WireLoopbackClient client in rejoined)
            {
                using ClientWorldSession world = new(server2.Runtime.Map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
                TerrainPersistenceLoopbackTests.ApplyAllFrames(client, world);
                Assert.Equal(serverChecksum, WorldStateChecksum.Compute(world.Map));
                Assert.Empty(world.Map.FindInvariantViolations());
            }

            foreach (WireLoopbackClient client in rejoined)
            {
                await server2.DisconnectAsync(client);
            }
        }

        Assert.False(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));
        RestartObservation after = Observe(db, accountsWired);
        foreach ((string name, Action<RestartObservation, RestartObservation> check) in Invariants)
        {
            try
            {
                check(before, after);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"Invariant '{name}' failed: {ex.Message}", ex);
            }
        }

    }

    // The integrity check's cost on the seeded World10k database (DREAM_LOAD_TEST=1): the numbers the
    // wave F3 report quotes. Also the whole bootstrap, with and without the check, for the start cost.
    private const double IntegrityCheckBudgetMilliseconds = 15.0;

    [LoadFact]
    public void World10k_IntegrityCheckCost()
    {
        string root = LoadPaths.FindRepoRoot();
        string work = Path.Combine(Path.GetTempPath(), $"dream-f3-integrity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        string databasePath = Path.Combine(work, "world.sqlite");
        string schemaPath = Path.Combine(root, "dream", "config", "player-profiles.init.sql");
        try
        {
            WorldSeeder.Seed(databasePath, schemaPath, LoadScenario.World10k);
            SqliteConnection.ClearAllPools();
            SqliteSchemaBootstrapper schema = new(databasePath, schemaPath);
            schema.EnsureSchema();
            long bytes = new FileInfo(databasePath).Length;

            List<double> standard = [];
            List<double> quick = [];
            WorldIntegrityReport? last = null;
            for (int run = 0; run < 7; run++)
            {
                using SqliteConnection connection = schema.CreateOpenConnection();
                WorldIntegrityReport report = WorldIntegrityCheck.Run(connection, WorldDescriptor.Default, new WorldIntegrityOptions { QuickCheck = false });
                last = report;
                standard.Add(report.Elapsed.TotalMilliseconds);
                WorldIntegrityReport quickReport = WorldIntegrityCheck.Run(
                    connection, WorldDescriptor.Default, new WorldIntegrityOptions());
                quick.Add(quickReport.Elapsed.TotalMilliseconds);
            }

            standard.Sort();
            quick.Sort();
            output.WriteLine($"World10k database {bytes / 1024} KiB. Integrity check (foreign_key_check + bounds + slots + kinds), warm, median of 7: {standard[3]:0.0} ms (min {standard[0]:0.0}, max {standard[^1]:0.0}).");
            output.WriteLine($"With quick_check added: median {quick[3]:0.0} ms (min {quick[0]:0.0}, max {quick[^1]:0.0}).");
            output.WriteLine(string.Join(" | ", last!.ToLogLines(WorldDescriptor.Default)));
            Assert.False(last.IsFatal);
            // A budget, not just a printout: the check runs on every start, so a regression that makes
            // it scan a table per row would show here.
            Assert.True(standard[3] < IntegrityCheckBudgetMilliseconds, $"integrity check median {standard[3]:0.0} ms");

            // The first run in a fresh process-like state is the cold cost: reopen after pools are cleared.
            SqliteConnection.ClearAllPools();
            using SqliteConnection cold = new SqliteSchemaBootstrapper(databasePath, schemaPath).CreateOpenConnection();
            WorldIntegrityReport coldReport = WorldIntegrityCheck.Run(cold, WorldDescriptor.Default);
            output.WriteLine($"Cold connection: {coldReport.Elapsed.TotalMilliseconds:0.0} ms");

            Stopwatch boot = Stopwatch.StartNew();
            using (ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath))
            {
            }

            boot.Stop();
            output.WriteLine($"Whole CreatePersistenceBundle (schema check, world resolve, lease, integrity check): {boot.Elapsed.TotalMilliseconds:0.0} ms");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static DedicatedServerHost Bootstrap(MigrationTestDatabase db, out List<string> log)
    {
        List<string> lines = [];
        DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () => ServerBootstrap.CreateAuthoritativeHost(
                ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, log: lines.Add)),
            lines.Add,
            out int exitCode);
        Assert.True(host != null, $"exit {exitCode}: {string.Join(" | ", lines)}");
        log = lines;
        return host;
    }

    private static RestartObservation Observe(MigrationTestDatabase db, bool accountsWired)
    {
        SqliteConnection.ClearAllPools();
        ObjectIdTestSupport.PersistedIds ids = ObjectIdTestSupport.ReadIds(db.DatabasePath);
        Dictionary<string, long[]> items = [];
        List<string> profiles = [];
        Dictionary<long, string> itemKinds = [];
        List<string> enemyKinds = [];
        Dictionary<string, long?> owners = [];
        int accountCount;
        int accountsOverLimit;
        string integrity;
        WorldIntegrityReport report;
        using (SqliteConnection connection = db.Schema.CreateOpenConnection())
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT owner_player_name, item_instance_id FROM item_instances WHERE world_id = 1 AND owner_player_name IS NOT NULL ORDER BY owner_player_name, item_instance_id;";
                using SqliteDataReader reader = command.ExecuteReader();
                Dictionary<string, List<long>> grouped = [];
                while (reader.Read())
                {
                    string player = reader.GetString(0);
                    if (!grouped.TryGetValue(player, out List<long>? list))
                    {
                        grouped[player] = list = [];
                    }

                    list.Add(reader.GetInt64(1));
                }

                foreach ((string player, List<long> list) in grouped)
                {
                    items[player] = [.. list];
                }
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT player_name FROM player_profiles;";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    profiles.Add(reader.GetString(0));
                }
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT item_instance_id, item_kind FROM item_instances WHERE world_id = 1;";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    itemKinds[reader.GetInt64(0)] = reader.GetString(1);
                }
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT enemy_kind FROM enemy_instances WHERE world_id = 1;";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    enemyKinds.Add(reader.GetString(0));
                }
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT p.player_name, p.account_id FROM player_profiles AS p;";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    owners[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                }
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM accounts;";
                accountCount = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM (SELECT account_id FROM player_profiles WHERE account_id IS NOT NULL GROUP BY account_id HAVING COUNT(*) > 5);";
                accountsOverLimit = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                integrity = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
            }

            report = WorldIntegrityCheck.Run(
                connection,
                WorldDescriptor.Default,
                ServerBootstrap.IntegrityOptionsFor(ContentCatalog.Shipped) with { QuickCheck = false });
        }

        SqliteConnection.ClearAllPools();
        return new RestartObservation(ids, integrity, report, items, profiles, itemKinds, enemyKinds, owners, accountCount, accountsOverLimit, accountsWired);
    }
}
