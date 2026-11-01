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

    // Plan I-D18 and the glue fix: a push waits while an inventory result is queued. That queue is
    // server-wide, so one player's inventory actions on every tick must not hold back another player's loot.
    private static async Task<(int Steps, int GrieferInventoryResults)> StepsUntilThePushAsync(bool grief)
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        server.Runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1);
        await using HeadlessPlayScene victim = await HeadlessPlayScene.JoinAsync(server, "adv-victim");
        await using HeadlessPlayScene griefer = await HeadlessPlayScene.JoinAsync(server, "adv-griefer");
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(server.Runtime, "adv-victim");
        IAuthoritativePlayerTarget other = WaveIRuntimeKit.Target(server.Runtime, "adv-griefer");
        other.SetWorldPosition(new Vector3(48f, 1.5f, 48f));
        for (int i = 0; i < 8; i++)
        {
            await server.TickAsync();
        }

        int pushStep = 0;
        for (int step = 1; step <= 60; step++)
        {
            if (server.Runtime.Enemies.ActiveActors.FirstOrDefault() is { } wolf)
            {
                wolf.SetPosition(target.EyePosition + (target.Facing * 2f) - new Vector3(0f, Dream.Simulation.Enemies.WolfDefinition.CombatAnchorHeight, 0f));
                wolf.Combatant.Health.SetAuthoritativeState(1, wolf.Combatant.Health.MaxHealth);
            }

            if (grief)
            {
                await griefer.SendActionAsync(new InventoryMoveIntent(0, 1));
            }

            await victim.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: true);
            await server.TickAsync();
            Assert.True(await victim.FlushAsync());
            Assert.True(await griefer.FlushAsync());
            victim.Pump();
            griefer.Pump();
            if (pushStep == 0 && victim.Applied.OfType<InventoryPushMessage>().Any())
            {
                pushStep = step;
            }

            // A few more ticks of the neighbour acting, so its results have been delivered too.
            if (pushStep != 0 && step >= pushStep + 6)
            {
                break;
            }
        }

        return (pushStep == 0 ? int.MaxValue : pushStep, griefer.Applied.OfType<ServerActionMessage>().Count(m => m.Result.Payload is InventoryChangedResult));
    }

    [Fact]
    public async Task AnotherPlayersInventoryActionOnEveryTick_DoesNotHoldBackAKillersPush()
    {
        (int control, _) = await StepsUntilThePushAsync(grief: false);
        (int griefed, int results) = await StepsUntilThePushAsync(grief: true);

        output.WriteLine($"push after {control} steps alone, {griefed} steps with a neighbour acting every tick ({results} inventory results seen)");
        Assert.True(control < 60, $"the control never got its push ({control})");
        Assert.True(results > 0, "the neighbour's inventory actions never produced inventory results: the test did not attack what it claims to");
        Assert.True(griefed <= control + 4, $"the push took {griefed} steps against {control} when a neighbour acted every tick");
    }
}
