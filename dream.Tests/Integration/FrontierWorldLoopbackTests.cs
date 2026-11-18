using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Application;
using Dream.Client.Replication;
using Dream.Client.Scenes;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Integration;

// M9: the 100 m frontier world over real TCP on a real database with the shipped content. A new dedicated-server
// world is 100 x 8 x 100 (ServerStartOptions' NewWorldSize), its camps stand at their absolute homes with the
// tiered enemies, the warden gives the tier quests, and a 56 m world on the same build keeps the four old camps
// and four quests. The join is measured, not pinned.
public sealed class FrontierWorldLoopbackTests(ITestOutputHelper output)
{
    private const string Session = "frontier-e2e";
    private const string FrontierWorld = "frontier";
    private const string TierQuest = "ash-on-the-wind";
    private const string Prowler = "AshenProwler";

    // wave D/E baseline: a 100 m world with 10k cubes and 1k rubble joined in 232,677 B. An unedited world is smaller.
    private const long EditedHundredMeterJoinBytes = 232_677;

    // Every camp of the <= 99 m homeland, in the order spawns.json lists them. A 100 m world holds none of them.
    private static readonly string[] HomelandCampKeys =
    [
        "north-east-wolves", "south-west-wolves", "south-east-dire-wolf", "moonlit-dire-wolves",
        "north-ridge-wolves", "old-road-wolves", "west-plain-wolves", "east-wood-wolves",
        "south-mire-wolves", "south-fen-dire", "night-pack-west", "night-ridge-dire"
    ];

    private static readonly string HomelandCampKeySql =
        string.Join(",", HomelandCampKeys.Select(key => $"'{key}'"));

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db, ServerStartOptions start, bool npcs = false)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: start));
        WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        // Before the first tick: overrides the test assembly's camp-less default content.
        server.Runtime.EnemyContent = ContentCatalog.Shipped;
        if (npcs)
        {
            server.Runtime.SpawnContentNpcs();
        }

        return server;
    }

    private static ServerStartOptions Frontier => new(FrontierWorld, WorldDefaults.ServerNewWorldSize);

    // A tick without the quest tests' pen: the enemies stay where the camps put them.
    private static async Task PlainStepAsync(WireLoopbackServer server, HeadlessPlayScene client, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
            await server.TickAsync();
            Assert.True(await client.FlushAsync());
            await client.PumpToSnapshotAsync(server.Runtime.CurrentServerTick + 1 - (ulong)server.Service.SnapshotIntervalTicks);
        }

        client.Pump();
    }

    private static IEnumerable<SpawnCamp> CampsNow(WorldSessionRuntime runtime)
    {
        bool night = runtime.WorldClock.Phase == WorldDayPhase.Night;
        return ContentCatalog.Shipped.Camps.Where(camp => camp.ExistsOn(runtime.Map.Width) && (!camp.Night || night));
    }

    private static float Distance(Vector3 position, SpawnCamp camp) =>
        (position.Xz - new Vector2(camp.X, camp.Z)).Length;

    // The members of a camp: its template's enemies whose home is within a ring of its centre.
    private static List<EnemyActor> MembersOf(WorldSessionRuntime runtime, SpawnCamp camp) =>
        [.. runtime.Enemies.ActiveActors.Where(enemy =>
            enemy.IsActive && enemy.EnemyKind == camp.Template && Distance(enemy.HomePosition, camp) < 8f)];

    private void AssertCampsAtTheirAbsoluteHomes(WorldSessionRuntime runtime)
    {
        List<SpawnCamp> expected = [.. CampsNow(runtime)];
        Assert.NotEmpty(expected);
        foreach (SpawnCamp camp in expected)
        {
            Assert.True(camp.IsAbsolute, camp.Key);
            Assert.Equal(camp.Count, MembersOf(runtime, camp).Count);
        }

        Assert.Equal(expected.Sum(camp => camp.Count), runtime.Enemies.ActiveActors.Count(enemy => enemy.IsActive));
    }

    [Fact]
    public async Task ANewServerWorld_Is100Meters_AndItsJoinSizeIsMeasured()
    {
        Assert.True(Program.TrySelectProcessMode(["--server"], out ProcessMode mode, out ServerStartOptions options, out _));
        Assert.Equal(ProcessMode.Server, mode);
        Assert.Equal(100, options.NewWorldSize);
        Assert.Equal(WorldDefaults.ServerNewWorldSize, options.NewWorldSize);

        // The default path: a fresh database (migration 0003 seeds world 1 at 56 m) and the bare --server options.
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db, options);
        Assert.Equal(100, server.Runtime.Map.Width);
        Assert.Equal(100, server.Runtime.Map.Depth);
        Assert.Equal(100, Convert.ToInt64(db.Scalar("SELECT width FROM worlds WHERE world_id = 1")));

        await using WireLoopbackClient client = await server.ConnectAndJoinAsync("frontier-join", awaitPrefetchBeforeTicking: true);
        for (int i = 0; i < 3; i++)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }

        // The join: every frame but the snapshots (their size follows the actors, not the join).
        List<WireFrame> frames = [.. client.Frames.Where(frame => frame.Decoded.Envelope.MessageKind != "server.snapshot")];
        long joinBytes = frames.Sum(frame => (long)frame.Payload.Length);
        output.WriteLine($"100 m frontier join: {frames.Count} frames, {joinBytes:N0} bytes, kinds {string.Join(",", frames.Select(frame => frame.Decoded.Envelope.MessageKind))}");
        Assert.True(joinBytes < EditedHundredMeterJoinBytes, $"{joinBytes} B");
        Assert.DoesNotContain(frames, frame => frame.Decoded.Envelope.MessageKind.Contains("terrain", StringComparison.Ordinal));

        using ClientWorldSession world = new(server.Runtime.Map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        TerrainPersistenceLoopbackTests.ApplyAllFrames(client, world);
        Assert.Equal(100, world.Map.Width);
        Assert.Equal(100, world.Map.Depth);
        Assert.Equal(WorldStateChecksum.Compute(server.Runtime.Map), WorldStateChecksum.Compute(world.Map));
        await server.DisconnectAsync(client);
    }

    [Fact]
    public async Task TheFrontierCamps_StandAtTheirAbsoluteHomes_TheWardenGivesTheTierQuests_AKillPaysOut_AndARestartRestoresTheHomes()
    {
        using MigrationTestDatabase db = new();
        string key = M6EndToEndLoopbackTests.KeyOf(Session);
        QuestDefinition quest = ContentCatalog.Shipped.Quests.Single(q => q.Key == TierQuest);
        Assert.True(ContentCatalog.Shipped.TryGetEnemy(Prowler, out EnemyTemplate? template));
        Assert.True(ContentCatalog.Shipped.TryGetLootTable(template.LootTable!, out LootTable table));
        Assert.True(table.Drops >= 2);

        int enemiesBefore;
        await using (WireLoopbackServer server = await StartAsync(db, Frontier, npcs: true))
        {
            await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, Session);
            HeadlessPlayScene[] clients = [a];
            WorldSessionRuntime runtime = server.Runtime;
            Assert.Equal(100, runtime.Map.Width);

            await PlainStepAsync(server, a, 12);

            // The camps filled at their absolute homes; the client sees the same enemies with names and levels.
            AssertCampsAtTheirAbsoluteHomes(runtime);
            enemiesBefore = runtime.Enemies.ActiveActors.Count(enemy => enemy.IsActive);
            Assert.Equal(enemiesBefore, a.World.Enemies.ActiveActors.Count(enemy => enemy.IsActive));
            foreach (SpawnCamp camp in CampsNow(runtime))
            {
                Assert.True(ContentCatalog.Shipped.TryGetEnemy(camp.Template, out EnemyTemplate? campTemplate));
                List<EnemyActor> shown = [.. a.World.Enemies.ActiveActors.Where(enemy =>
                    enemy.IsActive && enemy.DisplayName == campTemplate.DisplayName && Distance(enemy.Position, camp) < 8f)];
                Assert.Equal(camp.Count, shown.Count);
                Assert.All(shown, enemy => Assert.Equal(campTemplate.Level, enemy.Level));
            }

            // No legacy camp exists on this world: not in the content for 100 m, and no enemy near an old home.
            Assert.DoesNotContain(CampsNow(runtime), camp => HomelandCampKeys.Contains(camp.Key));
            Assert.Contains(runtime.Enemies.ActiveActors, enemy => enemy.EnemyKind == "GraveHowler");
            await M6EndToEndLoopbackTests.FlushAsync(server);
            Assert.Equal(0, Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM enemy_instances WHERE camp_key IN ({HomelandCampKeySql})")));
            Assert.True(Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM enemy_instances WHERE camp_key LIKE 'frontier-%'")) >= enemiesBefore);

            // The vendor and the warden are within reach of the spawn.
            Assert.Equal(2, a.Npcs.Count);
            Assert.True(a.Npcs.TryGet(M6EndToEndLoopbackTests.WardenActorId, out ClientNpc warden));
            Assert.True((warden.Position.Xz - new Vector2(5f, 5f)).Length < 10f, warden.Position.ToString());

            // The warden offers the tier quests, which the 56 m world does not.
            await M6EndToEndLoopbackTests.PlaceAsync(server, clients, a, M6EndToEndLoopbackTests.BesideTheWarden(a));
            await M6EndToEndLoopbackTests.OpenDialogueAsync(server, clients, a);
            Assert.Equal(
                ContentCatalog.Shipped.Quests.Where(q => q.ExistsOn(100)).Select(q => q.Key),
                M6EndToEndLoopbackTests.Hud(a).Offers.Select(o => o.Key));
            Assert.Equal(7, M6EndToEndLoopbackTests.Hud(a).Offers.Count);

            // Take the quest, kill three replicated Ashen Prowlers through the real attack path.
            M6EndToEndLoopbackTests.AssertAccepted(await M6EndToEndLoopbackTests.ResultAsync(
                server, clients, a, M6EndToEndLoopbackTests.Click(a, new QuestAcceptIntent(M6EndToEndLoopbackTests.WardenActorId, TierQuest))));
            await M6EndToEndLoopbackTests.StepUntilAsync(server, clients, () => M6EndToEndLoopbackTests.Hud(a).Entries.Count == 1, "the quest log");
            long goldBefore = runtime.InventoryService.GoldOf(key);
            PlayerCombatant combatant = M6EndToEndLoopbackTests.Target(server, Session).Combatant;
            (int Level, int Experience) before = (combatant.Level, combatant.Experience);

            for (int kill = 1; kill <= 3; kill++)
            {
                await M6EndToEndLoopbackTests.KillAsync(server, clients, a, Prowler);
                await M6EndToEndLoopbackTests.StepUntilAsync(
                    server, clients, () => M6EndToEndLoopbackTests.Entry(a, TierQuest).Objectives[0].Have == kill, $"kill {kill} in the log");
            }

            Assert.True(M6EndToEndLoopbackTests.Entry(a, TierQuest).Ready);
            long killGold = runtime.InventoryService.GoldOf(key) - goldBefore;
            Assert.InRange(killGold, 3L * template.GoldMin, 3L * template.GoldMax);

            // Turn in at the still open dialogue: paid once, with gold and XP.
            Assert.True(M6EndToEndLoopbackTests.Hud(a).DialogueOpen);
            Assert.Equal(QuestOfferState.ReadyToTurnIn, Assert.Single(M6EndToEndLoopbackTests.Hud(a).Offers, o => o.Key == TierQuest).State);
            M6EndToEndLoopbackTests.AssertAccepted(await M6EndToEndLoopbackTests.ResultAsync(
                server, clients, a, M6EndToEndLoopbackTests.Click(a, new QuestTurnInIntent(M6EndToEndLoopbackTests.WardenActorId, TierQuest))));
            await M6EndToEndLoopbackTests.StepUntilAsync(server, clients, () => M6EndToEndLoopbackTests.Entry(a, TierQuest).Completed, "the completed quest");
            Assert.Equal(goldBefore + killGold + quest.Rewards.Gold, runtime.InventoryService.GoldOf(key));
            Assert.True(combatant.Level > before.Level || combatant.Experience > before.Experience);

            await M6EndToEndLoopbackTests.FlushAsync(server);
            Assert.Equal(1, Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM player_quests WHERE player_name = '{key}' AND quest_key = '{TierQuest}' AND state = 2")));
            await a.LeaveAsync();
            await M6EndToEndLoopbackTests.FlushAsync(server);
        }

        // A restart opens the stored 100 m world and puts every camp member back at its camp's absolute home.
        SqlitePools.Release(db.Schema);
        await using (WireLoopbackServer again = await StartAsync(db, Frontier, npcs: true))
        {
            await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(again, Session);
            Assert.Equal(100, again.Runtime.Map.Width);
            await PlainStepAsync(again, a, 12);

            AssertCampsAtTheirAbsoluteHomes(again.Runtime);
            await a.LeaveAsync();
        }
    }

    [Fact]
    public async Task A56MeterWorld_OnTheSameBuild_HoldsExactlyTheTwelveHomelandCampsAndFourQuests()
    {
        Assert.Equal(HomelandCampKeys, ContentCatalog.Shipped.Camps.Where(camp => camp.ExistsOn(56)).Select(camp => camp.Key));
        Assert.Equal(4, ContentCatalog.Shipped.Quests.Count(q => q.ExistsOn(56)));
        Assert.All(ContentCatalog.Shipped.Camps.Where(camp => camp.ExistsOn(56)), camp => Assert.False(camp.IsAbsolute, camp.Key));

        // A stored 56 m world (it ran once and saved its seed) opened by the bare --server options stays 56.
        using MigrationTestDatabase db = new();
        await using (WireLoopbackServer first = await StartAsync(db, new ServerStartOptions(null)))
        {
            Assert.Equal(56, first.Runtime.Map.Width);
            await M6EndToEndLoopbackTests.FlushAsync(first);
        }

        SqlitePools.Release(db.Schema);
        Assert.True(Program.TrySelectProcessMode(["--server"], out _, out ServerStartOptions bare, out _));
        Assert.Equal(100, bare.NewWorldSize);
        await using WireLoopbackServer server = await StartAsync(db, bare, npcs: true);
        await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, Session);
        Assert.Equal(56, server.Runtime.Map.Width);
        Assert.Equal(56, Convert.ToInt64(db.Scalar("SELECT width FROM worlds WHERE world_id = 1")));
        await PlainStepAsync(server, a, 12);

        List<SpawnCamp> expected = [.. CampsNow(server.Runtime)];
        Assert.All(expected, camp => Assert.Contains(camp.Key, HomelandCampKeys));
        Assert.Equal(expected.Sum(camp => camp.Count), server.Runtime.Enemies.ActiveActors.Count(enemy => enemy.IsActive));
        Assert.DoesNotContain(server.Runtime.Enemies.ActiveActors, enemy => enemy.EnemyKind is Prowler or "GraveHowler");

        await M6EndToEndLoopbackTests.FlushAsync(server);
        Assert.Equal(0, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM enemy_instances WHERE camp_key LIKE 'frontier-%'")));

        await M6EndToEndLoopbackTests.PlaceAsync(server, [a], a, M6EndToEndLoopbackTests.BesideTheWarden(a));
        await M6EndToEndLoopbackTests.OpenDialogueAsync(server, [a], a);
        Assert.Equal(4, M6EndToEndLoopbackTests.Hud(a).Offers.Count);

        // A crafted accept or turn-in for a 100 m tier quest, next to the warden, is refused on this world.
        HeadlessPlayScene[] clients = [a];
        M6EndToEndLoopbackTests.AssertRejected(await M6EndToEndLoopbackTests.ResultAsync(
            server, clients, a, M6EndToEndLoopbackTests.Click(a, new QuestAcceptIntent(M6EndToEndLoopbackTests.WardenActorId, TierQuest))), "quest is not available");
        M6EndToEndLoopbackTests.AssertRejected(await M6EndToEndLoopbackTests.ResultAsync(
            server, clients, a, M6EndToEndLoopbackTests.Click(a, new QuestTurnInIntent(M6EndToEndLoopbackTests.WardenActorId, TierQuest))), "quest is not available");
        await M6EndToEndLoopbackTests.FlushAsync(server);
        Assert.Equal(0, Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM player_quests WHERE quest_key = '{TierQuest}'")));
        await a.LeaveAsync();
    }
}
