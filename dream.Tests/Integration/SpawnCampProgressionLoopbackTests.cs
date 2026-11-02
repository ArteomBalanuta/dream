using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Scenes;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Hosting;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.Rules;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Integration;

// M2 fix loop: the camps and the stat points together over real TCP on a real database, with the shipped
// content (the test assembly's default has no camps). The camps replicate to the client, a server-side
// level-up reaches the client's panel, a point is spent through the HUD's own path (SpendAction,
// ToInventoryIntent, QueuePlayerAction), and the stats survive a rejoin and a restart.
[Collection(TimingSensitiveCollection.Name)]
public sealed class SpawnCampProgressionLoopbackTests
{
    private const string Session = "camp-progress";

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        // Before the first tick: overrides the test assembly's camp-less default content.
        server.Runtime.EnemyContent = ContentCatalog.Shipped;
        return server;
    }

    private static async Task StepAsync(WireLoopbackServer server, HeadlessPlayScene client, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
            await server.TickAsync();
            Assert.True(await client.FlushAsync());
            await client.PumpToSnapshotAsync(server.Runtime.CurrentServerTick + 1 - (ulong)server.Service.SnapshotIntervalTicks);
        }
    }

    private static HudStatusSnapshot Panel(HeadlessPlayScene client) =>
        client.World.CapturePresentationState(64f).HudStatus;

    private static int ActiveServerEnemies(WireLoopbackServer server) =>
        server.Runtime.Enemies.ActiveActors.Count(enemy => enemy.IsActive);

    [Fact]
    public async Task CampsReplicate_ALevelUpReachesThePanel_AHudSpendIsApplied_AndTheStatsSurviveARejoinAndARestart()
    {
        using MigrationTestDatabase db = new();
        int camps;
        int serverStrength;
        await using (WireLoopbackServer server = await StartAsync(db))
        {
            Assert.NotEmpty(server.Runtime.EnemyContent.Camps);
            await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session);
            await StepAsync(server, client, 6);

            // The camps fill the world and the client sees the same enemies.
            camps = ActiveServerEnemies(server);
            Assert.InRange(camps, 5, 7);
            Assert.Equal(camps, client.World.Enemies.ActiveActors.Count(enemy => enemy.IsActive));

            // A server-side level-up: the snapshot carries the level and the server's threshold.
            IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(server.Runtime, Session);
            target.Combatant.ApplyProgression(3, 10);
            await StepAsync(server, client, 2);
            HudStatusSnapshot panel = Panel(client);
            Assert.Equal(3, panel.Level);
            Assert.Equal(10, panel.Experience);
            int required = CombatFormulaService.Default.EvaluateExperienceToNextLevel(3, 10);
            Assert.Equal(required, panel.ExperienceToNextLevel);
            Assert.Equal(10, CharacterPanelModel.PointsAvailable(panel));
            int strengthBefore = panel.BaseStrength;

            // The spend button's own path.
            InventoryHudAction action = CharacterPanelModel.SpendAction(CharacterStat.Strength)!.Value;
            client.Session.QueuePlayerAction(new PlayerActionMessage(
                new PlayerSessionId(client.SessionId),
                10_000,
                PlayScene.ToInventoryIntent(action)));
            Assert.True(await client.Session.FlushOutboundAsync(WireLoopbackServer.Timeout));
            for (int i = 0; i < 40 && target.Combatant.BaseStats.Strength == strengthBefore; i++)
            {
                await StepAsync(server, client);
            }

            await StepAsync(server, client, 3);
            serverStrength = target.Combatant.BaseStats.Strength;
            Assert.Equal(strengthBefore + 1, serverStrength);
            panel = Panel(client);
            Assert.Equal(serverStrength, panel.BaseStrength);
            Assert.Equal(9, CharacterPanelModel.PointsAvailable(panel));

            // A rejoin on the same server keeps them.
            await client.LeaveAsync();
            await using HeadlessPlayScene again = await HeadlessPlayScene.JoinAsync(server, Session);
            await StepAsync(server, again, 3);
            panel = Panel(again);
            Assert.Equal(3, panel.Level);
            Assert.Equal(serverStrength, panel.BaseStrength);
            await again.LeaveAsync();
        }

        // A restart keeps them too, and the camps are restored rather than doubled.
        SqliteConnection.ClearAllPools();
        await using (WireLoopbackServer restarted = await StartAsync(db))
        {
            await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(restarted, Session);
            await StepAsync(restarted, client, 6);
            HudStatusSnapshot panel = Panel(client);
            Assert.Equal(3, panel.Level);
            Assert.Equal(10, panel.Experience);
            Assert.Equal(serverStrength, panel.BaseStrength);
            Assert.Equal(camps, ActiveServerEnemies(restarted));
            await client.LeaveAsync();
        }
    }

    [Fact]
    public async Task ExperienceSavedUnderAnOlderSteeperCurve_IsLeveledAtJoin_AndSaved()
    {
        using MigrationTestDatabase db = new();
        await using (WireLoopbackServer first = await StartAsync(db))
        {
            await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(first, Session);
            await StepAsync(first, client, 2);
            await client.LeaveAsync();
        }

        SqliteConnection.ClearAllPools();
        SqlitePlayerProfileRepository profiles = new(db.Schema);
        PersistedPlayerProfile old = profiles.TryGet($"Name {Session}")!.Value;
        profiles.Save(old with { Stats = old.Stats with { Level = 1, Experience = 1500 } });

        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene joined = await HeadlessPlayScene.JoinAsync(server, Session);
        await StepAsync(server, joined, 3);
        HudStatusSnapshot panel = Panel(joined);
        Assert.True(panel.Level > 1);
        Assert.InRange(panel.Experience, 0, panel.ExperienceToNextLevel - 1);
        Assert.True(panel.ExperiencePercent < 100);
        Assert.Equal(CombatFormulaService.Default.EvaluateExperienceToNextLevel(panel.Level, panel.Experience), panel.ExperienceToNextLevel);

        await joined.LeaveAsync();
        PersistedPlayerProfile saved = profiles.TryGet($"Name {Session}")!.Value;
        Assert.Equal(panel.Level, saved.Stats.Level);
        Assert.Equal(panel.Experience, saved.Stats.Experience);
    }

    [Fact]
    public void TheClientShowsTheServersExperienceThreshold_NotItsOwnFormula()
    {
        SceneEventHub events = new();
        using ClientWorldSession session = new(56, new Vector3(5f, 1.5f, 5f), events);
        session.Player.Combatant.ApplyProgression(2, 10);
        int local = session.CapturePresentationState(64f).HudStatus.ExperienceToNextLevel;

        session.SetServerExperienceToNextLevel(local + 777);

        Assert.Equal(local + 777, session.CapturePresentationState(64f).HudStatus.ExperienceToNextLevel);
    }
}
