using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Integration;

// Wave I2: dying, waiting and respawning over real connections. A hand-written client that keeps sending
// movement, attacks and build and combat actions while dead changes nothing and is told no; the other
// client sees the dead player stay put and then return at the spawn with full health.
public sealed class RespawnLoopbackTests
{
    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath));
        return await WireLoopbackServer.StartAsync(host);
    }

    private static ActorSnapshot? Seen(WireLoopbackClient viewer, string session, ulong tick) =>
        viewer.Frames
            .Select(frame => frame.Decoded.Message)
            .OfType<ServerSnapshotMessage>()
            .Where(snapshot => snapshot.Snapshot.ServerTick == tick)
            .SelectMany(snapshot => snapshot.Snapshot.Actors)
            .Where(actor => actor.SessionId == session)
            .Cast<ActorSnapshot?>()
            .FirstOrDefault();

    [Fact]
    public async Task ADeadClientsMovementAttacksAndActions_ChangeNothingOverRealTcp_AndItRespawnsExactlyDelayTicksLater()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        WireLoopbackClient dead = await server.ConnectAndJoinAsync("mortal");
        WireLoopbackClient watcher = await server.ConnectAndJoinAsync("watcher");
        WorldSessionRuntime runtime = server.Runtime;
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, "mortal");
        target.SetWorldPosition(WaveIRuntimeKit.FarStart);
        await server.TickAsync();
        Assert.True(await server.FlushAllAsync());
        Vector3 deathPosition = target.Position;

        WaveIRuntimeKit.Kill(runtime, "mortal");
        ulong seenTick = runtime.CurrentServerTick + 1;
        ulong respawnTick = seenTick + PlayerRespawnRules.DelayTicks;
        List<ulong> sentActions = [];
        ulong serverSequence = 1_000;
        while (runtime.CurrentServerTick < respawnTick)
        {
            // Hold forward, jump and attack every tick; every 40 ticks also send the hostile actions.
            ulong tickAboutToRun = runtime.CurrentServerTick + 1;
            await dead.SendCommandAsync(PlayerInteractionMode.Combat, new PlayerMoveIntent(1f, 0f, JumpPressed: true), Vector3.UnitZ, attack: true);
            if (tickAboutToRun % 40 == 0 && tickAboutToRun < respawnTick)
            {
                await dead.SendActionAsync(new CombatUseIntent(target.EyePosition, target.Facing));
                sentActions.Add(dead.SentActionSequence);
                await dead.SendActionAsync(new BuildUseIntent(target.Position + new Vector3(1f, 0.5f, 0f)));
                sentActions.Add(dead.SentActionSequence);

                // The server-originated build and combat use (what a held button becomes) reaches the dead gate.
                Assert.True(server.Host.TryEnqueueServerAction(new PlayerActionMessage(
                    new PlayerSessionId("mortal"), ++serverSequence, new BuildUseIntent(target.Position + new Vector3(1f, 0.5f, 0f)))));
                sentActions.Add(serverSequence);
                Assert.True(server.Host.TryEnqueueServerAction(new PlayerActionMessage(
                    new PlayerSessionId("mortal"), ++serverSequence, new CombatUseIntent(target.EyePosition, target.Facing))));
                sentActions.Add(serverSequence);
            }

            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
            if (runtime.CurrentServerTick < respawnTick)
            {
                Assert.False(target.IsAlive, $"alive at tick {runtime.CurrentServerTick}, before {respawnTick}");
                Assert.True(Vector3.Distance(target.Position, deathPosition) < 0.3f, $"moved to {target.Position} while dead");
            }
        }

        // On its tick: alive, at the spawn, with full health.
        Assert.True(target.IsAlive);
        Assert.Equal(target.Combatant.Health.MaxHealth, target.Combatant.Health.CurrentHealth);
        Assert.True(Vector3.Distance(target.Position, new Vector3(5f, 1.5f, 5f)) < 1f, $"respawned at {target.Position}");

        // Every hostile action was answered with a rejection and none was accepted.
        await dead.WaitForAsync(
            frames => sentActions.All(sequence => frames.Any(f => f.Decoded.Message is ServerActionMessage m && m.Result.ActionSequence == sequence)),
            "answers to the hostile actions");
        List<ServerActionMessage> answers = [.. dead.Frames
            .Select(frame => frame.Decoded.Message)
            .OfType<ServerActionMessage>()
            .Where(m => sentActions.Contains(m.Result.ActionSequence))];
        Assert.NotEmpty(answers);
        Assert.All(answers, answer => Assert.False(answer.Result.Accepted));
        Assert.Contains(answers, answer => answer.Result.ActionSequence > 1_000 && answer.Result.Rejection!.Reason.Contains("dead", StringComparison.OrdinalIgnoreCase));
        // Both server-originated kinds were answered "dead".
        Assert.True(answers.Count(answer => answer.Result.ActionSequence > 1_000 && answer.Result.Rejection!.Reason.Contains("dead", StringComparison.OrdinalIgnoreCase)) >= 2);

        // The other client saw it dead where it fell the tick after, and alive again on the respawn tick.
        await watcher.WaitForSnapshotTickAsync(respawnTick);
        ActorSnapshot waiting = Seen(watcher, "mortal", respawnTick - 1)!.Value;
        Assert.False(waiting.IsAlive);
        Assert.Equal(0, waiting.CurrentHealth);
        Assert.True(Vector3.Distance(waiting.Position, deathPosition) < 0.3f);
        ActorSnapshot back = Seen(watcher, "mortal", respawnTick)!.Value;
        Assert.True(back.IsAlive);
        Assert.Equal(back.MaxHealth, back.CurrentHealth);
    }
}
