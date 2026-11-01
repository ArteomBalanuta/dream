using System.Reflection;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Integration;

// Combat text, end to end over loopback TCP with a real PlayScene (no GL) as the client. Counts the
// DamageAppliedEvents the client's event hub sees for each local hit: the floating number system
// spawns one number per event, so this is the number of numbers per hit.
[Collection(TimingSensitiveCollection.Name)]
public sealed class CombatTextLoopbackTests(ITestOutputHelper output)
{
    private const string Session = "combat-text";
    private static readonly Vector3 Pen = new(45f, 1f, 45f);

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        return await WireLoopbackServer.StartAsync(host);
    }

    private static SceneEventHub ClientEvents(HeadlessPlayScene client) =>
        (SceneEventHub)typeof(Dream.Client.Scenes.PlayScene)
            .GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client.Scene)!;

    private static async Task StepAsync(WireLoopbackServer server, HeadlessPlayScene client, EnemyActor? except, bool attack)
    {
        foreach (EnemyActor enemy in server.Runtime.Enemies.ActiveActors)
        {
            if (!ReferenceEquals(enemy, except))
            {
                enemy.SetPosition(Pen);
            }
        }

        await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack);
        await server.TickAsync();
        Assert.True(await client.FlushAsync());
        await client.PumpToSnapshotAsync(server.Runtime.CurrentServerTick + 1 - (ulong)server.Service.SnapshotIntervalTicks);
    }

    [Fact]
    public async Task OneLocalHit_PublishesExactlyOneDamageAppliedEventOnTheClient()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene client = await HeadlessPlayScene.JoinAsync(server, Session);

        List<(int ActorId, int Amount, FloatingCombatTextKind Kind)> seen = [];
        using IDisposable subscription = ClientEvents(client).Subscribe<DamageAppliedEvent>(
            evt => seen.Add((evt.Actor.Id, evt.Amount, evt.Kind)));

        await StepAsync(server, client, except: null, attack: false);
        EnemyActor enemy = server.Runtime.Enemies.ActiveActors.First();
        int enemyId = enemy.Combatant.Actor.Id;
        int hits = 0;
        int numbers = 0;
        for (int round = 0; round < 6; round++)
        {
            // Full health, in front of the player: one press lands one hit.
            IAuthoritativePlayerTarget target = WaveIRuntimeKitTarget(server);
            enemy.SetPosition(target.EyePosition + (target.Facing * 2f) - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f));
            enemy.Combatant.Health.SetAuthoritativeState(enemy.Combatant.Health.MaxHealth, enemy.Combatant.Health.MaxHealth);
            int healthBefore = enemy.Combatant.Health.CurrentHealth;
            int seenBefore = seen.Count;

            await StepAsync(server, client, enemy, attack: true);
            for (int i = 0; i < 6; i++)
            {
                await StepAsync(server, client, enemy, attack: false);
            }

            int lost = healthBefore - enemy.Combatant.Health.CurrentHealth;
            int forEnemy = seen.Skip(seenBefore).Count(e => e.ActorId == enemyId && e.Amount > 0);
            output.WriteLine($"round {round}: server health lost {lost}, client events for the enemy {forEnemy} amounts [{string.Join(",", seen.Skip(seenBefore).Where(e => e.ActorId == enemyId).Select(e => e.Amount))}]");
            if (lost > 0)
            {
                hits++;
                numbers += forEnemy;
            }

            // Out of the way, past the attack cooldown.
            enemy.SetPosition(Pen);
            for (int i = 0; i < 40; i++)
            {
                await StepAsync(server, client, enemy, attack: false);
            }
        }

        output.WriteLine($"hits {hits}, DamageAppliedEvents on the client {numbers}");
        Assert.True(hits >= 1, "no hit ever landed");
        Assert.Equal(hits, numbers);
    }

    private static IAuthoritativePlayerTarget WaveIRuntimeKitTarget(WireLoopbackServer server) =>
        Dream.Tests.Simulation.Runtime.WaveIRuntimeKit.Target(server.Runtime, Session);
}
