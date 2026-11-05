using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Server.Worlds;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Integration;

// Wave I tester, round 4, over real TCP with a real PlayScene (no GL): spawn protection across a leave
// and a rejoin of the same session.
public sealed class WolfBiteLoopbackTests(ITestOutputHelper output)
{
    private const string Session = "adv4-tcp";
    private static readonly Vector3 Far = new(45f, 1f, 45f);

    private static async Task StepAsync(WireLoopbackServer server, HeadlessPlayScene client)
    {
        await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
        await server.TickAsync();
        Assert.True(await client.FlushAsync());
        await client.PumpToSnapshotAsync(server.Runtime.CurrentServerTick + 1 - (ulong)server.Service.SnapshotIntervalTicks);
    }

    [Fact]
    public async Task AProtectedPlayerWhoLeavesAndRejoins_GetsItsOwnWindowOnReturn_OverRealTcp()
    {
        using MigrationTestDatabase db = new();
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        EnemyActor wolf = server.Runtime.Enemies.ActiveActors.First(e => e.IsActive);

        HeadlessPlayScene first = await HeadlessPlayScene.JoinAsync(server, Session);
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(server.Runtime, Session);
        wolf.SetPosition(Far);
        WaveIRuntimeKit.Kill(server.Runtime, Session);
        int guard = 0;
        while (!target.IsAlive)
        {
            wolf.SetPosition(Far);
            await StepAsync(server, first);
            Assert.True(++guard < 1000);
        }

        int max = target.Combatant.Health.MaxHealth;
        Assert.Equal(max, target.Combatant.Health.CurrentHealth);

        // Inside the window, a wolf right beside the player does nothing, and the client sees full health.
        // (A wolf with no target walks home, so the player is put beside the wolf's home.)
        WaveIRuntimeKit.PlaceBeside(wolf, target, new Vector3(1.2f, -0.5f, 0f));
        for (int i = 0; i < 60; i++)
        {
            await StepAsync(server, first);
            Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
            Assert.Equal(max, target.Combatant.Health.CurrentHealth);
        }

        await first.LeaveAsync();
        await first.DisposeAsync();
        wolf.SetPosition(Far);

        await using HeadlessPlayScene second = await HeadlessPlayScene.JoinAsync(server, Session);
        IAuthoritativePlayerTarget again = WaveIRuntimeKit.Target(server.Runtime, Session);
        Assert.True(again.IsAlive);
        // The rejoin is protected by its own full window (a join starts one), not the old respawn window that
        // had 130 ticks left: the wolf beside the player stays Idle for most of 192 ticks, then wakes.
        WaveIRuntimeKit.PlaceBeside(wolf, again, new Vector3(1.2f, -0.5f, 0f));
        await StepAsync(server, second);
        int stepsToWake = 1;
        while (wolf.BehaviorState == EnemyBehaviorState.Idle && stepsToWake < 400)
        {
            await StepAsync(server, second);
            stepsToWake++;
        }

        output.WriteLine($"rejoined at {again.Position}; the wolf left Idle after {stepsToWake} steps ({wolf.BehaviorState})");
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);
        // The window began when the join was accepted, a few ticks before JoinAsync returned.
        Assert.InRange(stepsToWake, 100, PlayerRespawnRules.SpawnProtectionTicks + 1);
        await second.LeaveAsync();
    }
}
