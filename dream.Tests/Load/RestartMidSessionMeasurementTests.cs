using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Server.Persistence;
using Dream.Simulation.Characters;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Integration;
using Dream.Tests.Simulation.Runtime;
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
// to RestartObservation if it needs a new read). Each entry is checked after the last restart against
// what the database held right after the stop before it. The server stops mid-session twice: run 2 adds
// rolled gear, so that stop is the one the entries compare with.
[Collection(TimingSensitiveCollection.Name)]
public sealed class RestartMidSessionMeasurementTests(ITestOutputHelper output)
{
    private static readonly string[] Sessions = ["restart-a", "restart-b", "restart-c"];

    // What run 2 grants and equips on the last session, one item for each equipment slot.
    private static readonly string[] RolledGear = ["gear.iron-blade", "gear.ward-lantern", "gear.leather-cap", "gear.leather-vest"];

    // Name, check. before: the database right after the stop that ends run 2. after: the database after
    // the restart server joined the same sessions and stopped again.
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
        ("ids stored when the second run stopped are not reissued by the third", (before, after) =>
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
        ("every session's profile and items (starter kit and gear) survive the last restart", (before, after) =>
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
        ("every character's stored rolls and equipment rows are unchanged by the restart", (before, after) =>
        {
            // Wave H. Run 2 gives one character four rolled, equipped items, so these are never empty sets.
            Assert.Contains(before.RollsByCharacter.Values, rolls => rolls.Length == RolledGear.Length);
            Assert.Contains(before.EquipmentByCharacter.Values, rows => rows.Length == RolledGear.Length);
            Assert.Equal(before.RollsByCharacter.Keys.Order(StringComparer.Ordinal), after.RollsByCharacter.Keys.Order(StringComparer.Ordinal));
            foreach ((string player, string[] rolls) in before.RollsByCharacter)
            {
                Assert.Equal(rolls, after.RollsByCharacter[player]);
            }

            Assert.Equal(before.EquipmentByCharacter.Keys.Order(StringComparer.Ordinal), after.EquipmentByCharacter.Keys.Order(StringComparer.Ordinal));
            foreach ((string player, string[] rows) in before.EquipmentByCharacter)
            {
                Assert.Equal(rows, after.EquipmentByCharacter[player]);
            }
        }),
        ("a rolled item a kill dropped stays with its character, byte for byte, across the restart", (before, after) =>
        {
            // Wave I2. Run 2 has restart-a kill the wolf under a loot table that always drops, so exactly one
            // character holds a rolled item in its backpack that it is not wearing (the geared character's are all worn).
            Assert.Contains(before.LootByCharacter.Values, rows => rows.Length == 1);
            Assert.Equal(before.LootByCharacter.Keys.Order(StringComparer.Ordinal), after.LootByCharacter.Keys.Order(StringComparer.Ordinal));
            foreach ((string player, string[] rows) in before.LootByCharacter)
            {
                Assert.Equal(rows, after.LootByCharacter[player]);
            }
        }),
        ("a character dead at the mid-session stop is alive after its wait in the next run", (before, after) =>
        {
            // Wave I2. Run 2 ends with restart-b killed and still waiting, so its stored health is 0; run 3 waits
            // out the respawn delay before the clients leave.
            Assert.Contains(before.HpByCharacter.Values, hp => hp == 0);
            foreach ((string player, int hp) in before.HpByCharacter)
            {
                if (hp == 0)
                {
                    Assert.True(after.HpByCharacter[player] > 0, $"{player} is still dead after the next run");
                }
            }
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
        bool AccountsWired,
        // Wave H2: per character, "itemId=storedRollText" of each item that has a stored roll (sorted), and
        // "slot=itemId" of each equipment row (slots 100..103, sorted by slot).
        IReadOnlyDictionary<string, string[]> RollsByCharacter,
        IReadOnlyDictionary<string, string[]> EquipmentByCharacter,
        // Wave I2: per character, "itemId=kind" of each rolled item in the backpack that is not worn (a kill's
        // loot), and the stored health.
        IReadOnlyDictionary<string, string[]> LootByCharacter,
        IReadOnlyDictionary<string, int> HpByCharacter);

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
        RestartObservation firstStop = Observe(db, accountsWired);
        output.WriteLine($"after the stop: {firstStop.Ids.AllObjects.Count} objects, {firstStop.ItemsByPlayer.Count} players with items, {firstStop.EnemyKinds.Count} enemies, counter {firstStop.Ids.NextObjectId}");
        Assert.Equal(Sessions.Length, firstStop.ItemsByPlayer.Count);

        // Run 2: restart and rejoin; each client must build the server's map. One character then gets
        // rolled gear in all four slots (H1's service on H2's repository, the equips as client intents),
        // and this run ends in a stop in the middle of the session too.
        Stopwatch secondStart = Stopwatch.StartNew();
        DedicatedServerHost host2 = Bootstrap(db, out List<string> log2);
        secondStart.Stop();
        Assert.Contains(log2, l => l.Contains("integrity check", StringComparison.Ordinal));
        output.WriteLine($"server start (bootstrap + host): first {firstStart.Elapsed.TotalMilliseconds:0.0} ms, restart {secondStart.Elapsed.TotalMilliseconds:0.0} ms");
        WireLoopbackServer server2 = await WireLoopbackServer.StartAsync(host2);
        List<WireLoopbackClient> rejoined = [];
        string geared;
        PlayerSessionId gearedSession;
        PlayerInventorySnapshot gearedInventory;
        try
        {
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

            AssertClientsBuiltTheServersMap(server2, rejoined);

            geared = WorldSessionRuntime.ResolveJoinPlayerName(Sessions[^1], $"Name {Sessions[^1]}");
            PlayerInventoryService inventory = server2.Runtime.InventoryService;
            for (int i = 0; i < RolledGear.Length; i++)
            {
                ItemRollSeed seed = new(1, server2.Runtime.CurrentServerTick, 7, 0, i);
                Assert.Equal(InventoryEquipOutcome.Equipped, inventory.TryGrantRolled(geared, RolledGear[i], ItemRarity.Rare, in seed));
                int slot = inventory.BuildSnapshot(geared).Slots.First(s => s.Item?.ItemKind == RolledGear[i]).SlotIndex;
                await rejoined[^1].SendActionAsync(new InventoryEquipIntent(slot));
                await server2.TickAsync();
                Assert.True(await server2.FlushAllAsync());
            }

            gearedSession = new(Sessions[^1]);
            gearedInventory = server2.Runtime.TryCaptureInventorySnapshot(gearedSession)!.Value;
            Assert.Equal(RolledGear[0], gearedInventory.EquippedSlot!.Value.Item!.Value.ItemKind);
            Assert.Equal(RolledGear[1..], gearedInventory.Equipment!.Select(row => row.Item!.Value.ItemKind));

            // Wave I2: restart-a kills the wolf under a loot table that always drops and gets a rolled item,
            // and restart-b is killed and is still waiting to respawn when the server stops.
            server2.Runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1);
            await KillAWolfAsync(server2, rejoined[0]);
            WaveIRuntimeKit.Kill(server2.Runtime, Sessions[1]);
            await server2.TickAsync();
            Assert.True(await server2.FlushAllAsync());
            Assert.False(WaveIRuntimeKit.Target(server2.Runtime, Sessions[1]).IsAlive);
        }
        finally
        {
            await server2.Service.DisposeAsync();
            server2.Runtime.DisposeWithoutFinalCheckpoint();
            server2.Host.Dispose();
            foreach (WireLoopbackClient client in rejoined)
            {
                await client.DisposeAsync();
            }
        }

        Assert.False(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default), "the second stop released the world");
        RestartObservation before = Observe(db, accountsWired);
        string healthWithGear = Convert.ToString(
            db.Scalar($"SELECT current_hp FROM player_world_state WHERE player_name = '{geared}';"),
            System.Globalization.CultureInfo.InvariantCulture)!;
        Assert.NotEmpty(healthWithGear);
        Assert.Equal(RolledGear.Length, before.RollsByCharacter[geared].Length);
        Assert.Equal(RolledGear.Length, before.EquipmentByCharacter[geared].Length);

        // What run 1 stored outlived run 2. The gear is new, so this is a subset check.
        Assert.Empty(firstStop.Ids.Items.Except(before.Ids.Items));
        Assert.Empty(firstStop.Ids.Cubes.Except(before.Ids.Cubes));
        Assert.True(before.Ids.NextObjectId >= firstStop.Ids.NextObjectId);

        // Run 2 changed only the geared session. The other two hold exactly what run 1 left, and the
        // geared one holds that plus exactly the rolled gear.
        foreach (string session in Sessions)
        {
            string player = WorldSessionRuntime.ResolveJoinPlayerName(session, $"Name {session}");
            Assert.Contains(player, firstStop.Profiles);
            Assert.Contains(player, before.Profiles);
            if (accountsWired)
            {
                Assert.Equal(firstStop.OwnerByCharacter[player], before.OwnerByCharacter[player]);
            }

            if (player == geared)
            {
                Assert.Equal(firstStop.ItemsByPlayer[player].Length + RolledGear.Length, before.ItemsByPlayer[player].Length);
                Assert.Empty(firstStop.ItemsByPlayer[player].Except(before.ItemsByPlayer[player]));
            }
            else if (player == WorldSessionRuntime.ResolveJoinPlayerName(Sessions[0], $"Name {Sessions[0]}"))
            {
                // Wave I2: the kill's loot is the one new item.
                Assert.Equal(firstStop.ItemsByPlayer[player].Length + 1, before.ItemsByPlayer[player].Length);
                Assert.Empty(firstStop.ItemsByPlayer[player].Except(before.ItemsByPlayer[player]));
            }
            else
            {
                Assert.Equal(firstStop.ItemsByPlayer[player], before.ItemsByPlayer[player]);
            }
        }

        foreach ((long item, string kind) in firstStop.ItemKinds)
        {
            Assert.Equal(kind, before.ItemKinds[item]);
        }

        // Run 3: restart and rejoin again; the gear is worn and the rows checked below are unchanged.
        DedicatedServerHost host3 = Bootstrap(db, out _);
        await using (WireLoopbackServer server3 = await WireLoopbackServer.StartAsync(host3))
        {
            List<WireLoopbackClient> again = [];
            foreach (string session in Sessions)
            {
                again.Add(await server3.ConnectAndJoinAsync(session));
            }

            for (int tick = 0; tick < 5; tick++)
            {
                await server3.TickAsync();
                Assert.True(await server3.FlushAllAsync());
            }

            AssertClientsBuiltTheServersMap(server3, again);

            // Wave I2: restart-b stopped dead; it respawns once its wait is over, with full health.
            string deadName = Sessions[1];
            IAuthoritativePlayerTarget revived = WaveIRuntimeKit.Target(server3.Runtime, deadName);
            Assert.False(revived.IsAlive, "restart-b should restore dead");
            for (int tick = 0; tick < PlayerRespawnRules.DelayTicks + 10 && !revived.IsAlive; tick++)
            {
                await server3.TickAsync();
                Assert.True(await server3.FlushAllAsync());
            }

            Assert.True(revived.IsAlive, "restart-b never respawned");
            Assert.Equal(revived.Combatant.Health.MaxHealth, revived.Combatant.Health.CurrentHealth);
            PlayerInventorySnapshot worn = server3.Runtime.TryCaptureInventorySnapshot(gearedSession)!.Value;
            Assert.Equal(gearedInventory.EquippedSlot!.Value.Item!.Value.ItemInstanceId, worn.EquippedSlot!.Value.Item!.Value.ItemInstanceId);
            Assert.Equal(
                gearedInventory.Equipment!.Select(row => row.Item!.Value.ItemInstanceId),
                worn.Equipment!.Select(row => row.Item!.Value.ItemInstanceId));

            // The rolled modifiers and rarity the client sees are the ones it saw before the restart.
            static string[] RollText(PlayerInventorySnapshot snapshot) => snapshot.Equipment!
                .Select(row => row.Item!.Value)
                .Prepend(snapshot.EquippedSlot!.Value.Item!.Value)
                .Select(item => $"{item.ItemInstanceId}:{item.Rarity}:{item.IsRolled}:" + string.Join(
                    ",", (item.Modifiers ?? []).Select(m => $"{m.Stat}/{m.Op}/{m.Value:R}")))
                .ToArray();
            Assert.Equal(RollText(gearedInventory), RollText(worn));

            foreach (WireLoopbackClient client in again)
            {
                await server3.DisconnectAsync(client);
            }
        }

        Assert.False(WorldHostLease.IsHeld(db.DatabasePath, WorldId.Default));
        RestartObservation after = Observe(db, accountsWired);
        Assert.Equal(
            healthWithGear,
            Convert.ToString(
                db.Scalar($"SELECT current_hp FROM player_world_state WHERE player_name = '{geared}';"),
                System.Globalization.CultureInfo.InvariantCulture));
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

    // Holds the attack with the wolf one step in front of the client at 1 HP until the kill's loot push arrives.
    private static async Task KillAWolfAsync(WireLoopbackServer server, WireLoopbackClient client)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            WaveIRuntimeKit.AimWolfAt(server.Runtime, client.SessionId, wolfHealth: 1);
            await client.SendCommandAsync(PlayerInteractionMode.Combat, PlayerMoveIntent.None, Vector3.UnitZ, attack: true);
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
            await client.WaitForSnapshotTickAsync(server.Runtime.CurrentServerTick);
            if (client.Frames.Any(frame => frame.Decoded.Message is InventoryPushMessage))
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException("the kill never produced loot");
    }

    private static void AssertClientsBuiltTheServersMap(WireLoopbackServer server, List<WireLoopbackClient> clients)
    {
        ulong serverChecksum = WorldStateChecksum.Compute(server.Runtime.Map);
        foreach (WireLoopbackClient client in clients)
        {
            using ClientWorldSession world = new(server.Runtime.Map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            TerrainPersistenceLoopbackTests.ApplyAllFrames(client, world);
            Assert.Equal(serverChecksum, WorldStateChecksum.Compute(world.Map));
            Assert.Empty(world.Map.FindInvariantViolations());
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
            SqlitePools.Release(databasePath);
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
            SqlitePools.Release(schema);
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
            SqlitePools.Release(databasePath);
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
        SqlitePools.Release(db.DatabasePath);
        ObjectIdTestSupport.PersistedIds ids = ObjectIdTestSupport.ReadIds(db.DatabasePath);
        Dictionary<string, long[]> items = [];
        List<string> profiles = [];
        Dictionary<long, string> itemKinds = [];
        List<string> enemyKinds = [];
        Dictionary<string, long?> owners = [];
        Dictionary<string, string[]> rolls = [];
        Dictionary<string, string[]> equipment = [];
        Dictionary<string, string[]> loot = [];
        Dictionary<string, int> hp = [];
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

            ReadGrouped(
                connection,
                "SELECT owner_player_name, item_instance_id || '=' || rolled_modifiers FROM item_instances " +
                "WHERE world_id = 1 AND owner_player_name IS NOT NULL AND rolled_modifiers IS NOT NULL ORDER BY owner_player_name, item_instance_id;",
                rolls);
            ReadGrouped(
                connection,
                "SELECT player_name, slot_index || '=' || item_instance_id FROM player_inventory_slots " +
                "WHERE world_id = 1 AND slot_index >= 100 ORDER BY player_name, slot_index;",
                equipment);

            ReadGrouped(
                connection,
                "SELECT owner_player_name, item_instance_id || '=' || item_kind FROM item_instances " +
                "WHERE world_id = 1 AND owner_player_name IS NOT NULL AND rolled_modifiers IS NOT NULL " +
                "AND item_instance_id NOT IN (SELECT item_instance_id FROM player_inventory_slots WHERE world_id = 1 AND slot_index >= 100) " +
                "ORDER BY owner_player_name, item_instance_id;",
                loot);
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT player_name, current_hp FROM player_world_state;";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    hp[reader.GetString(0)] = reader.GetInt32(1);
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

        SqlitePools.Release(db.DatabasePath);
        return new RestartObservation(ids, integrity, report, items, profiles, itemKinds, enemyKinds, owners, accountCount, accountsOverLimit, accountsWired, rolls, equipment, loot, hp);
    }

    // Rows of (key, value) in query order, grouped by key.
    private static void ReadGrouped(SqliteConnection connection, string sql, Dictionary<string, string[]> into)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, List<string>> grouped = [];
        while (reader.Read())
        {
            string key = reader.GetString(0);
            if (!grouped.TryGetValue(key, out List<string>? list))
            {
                grouped[key] = list = [];
            }

            list.Add(reader.GetString(1));
        }

        foreach ((string key, List<string> list) in grouped)
        {
            into[key] = [.. list];
        }
    }
}
