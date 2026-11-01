using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Server.Worlds;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Integration;

// Wave I tester, over real TCP with a real PlayScene (no GL): a death that lands while the client is
// still receiving the join's world.
[Collection(TimingSensitiveCollection.Name)]
public sealed class WaveIAdversarialLoopbackTests(ITestOutputHelper output)
{
    private const string Session = "adv-transfer";
    private static readonly Vector3 Pen = new(45f, 1f, 45f);

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        return await WireLoopbackServer.StartAsync(host);
    }

    private static async Task StepAsync(WireLoopbackServer server, HeadlessPlayScene client)
    {
        foreach (var enemy in server.Runtime.Enemies.ActiveActors)
        {
            enemy.SetPosition(Pen);
        }

        await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
        await server.TickAsync();
        Assert.True(await client.FlushAsync());
        await client.PumpToSnapshotAsync(server.Runtime.CurrentServerTick + 1 - (ulong)server.Service.SnapshotIntervalTicks);
    }

    [Fact]
    public async Task ADeathWhileTheClientStillReceivesTheJoinsWorld_IsShownDead_ThenRespawnsAtTheSpawnWithFullHealth()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session, pumpJoin: false);
        Assert.Empty(client.Applied);

        // Straight after the join tick, before the client has applied anything: the character dies.
        await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
        await server.TickAsync();
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(server.Runtime, Session);
        target.SetWorldPosition(new Vector3(30f, 1.5f, 30f));
        WaveIRuntimeKit.Kill(server.Runtime, Session);
        ulong diedAt = server.Runtime.CurrentServerTick;

        bool sawDeadOverlay = false;
        bool sawAliveAgain = false;
        for (int i = 0; i < PlayerRespawnRules.DelayTicks + 40; i++)
        {
            await StepAsync(server, client);
            if (client.Applied.Any(message => message is JoinAcceptedMessage))
            {
                sawDeadOverlay |= client.Death.IsDead;
                sawAliveAgain |= sawDeadOverlay && !client.Death.IsDead;
            }
        }

        output.WriteLine($"died at tick {diedAt}, now {server.Runtime.CurrentServerTick}; overlay seen {sawDeadOverlay}, cleared {sawAliveAgain}; transfers {client.Session.WorldTransfersCompleted}");
        Assert.True(client.Session.WorldTransfersCompleted >= 1);
        Assert.True(sawDeadOverlay, "the client never showed the death");
        Assert.True(sawAliveAgain, "the client never cleared the death after the respawn");
        Assert.True(target.IsAlive);
        Assert.Equal(target.Combatant.Health.MaxHealth, target.Combatant.Health.CurrentHealth);
        Assert.True(client.World.Player.Combatant.Health.IsAlive);
        Assert.Equal(target.Combatant.Health.MaxHealth, client.World.Player.Combatant.Health.CurrentHealth);
        Vector3 serverAt = target.Position;
        Assert.True(Vector2.Distance(new Vector2(serverAt.X, serverAt.Z), new Vector2(5f, 5f)) < 2f, $"server {serverAt}");
        await client.LeaveAsync();
    }
}
