using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence;

namespace Dream.Tests.Integration;

// Wave 13 tester, found while checking RuntimePersistenceBridge end to end: a player's world state
// (player_world_state: position, facing and current HP) is written only by checkpoints, and only
// for players that are still attached. DetachRemoteSession saves the profile (level, XP, profile
// HP) but not the world state, and the server's shutdown (DedicatedServerService dispose, then the
// host's final checkpoint) detaches every session before that final checkpoint runs. On the next
// join TryLoadPlayerForRestore prefers the world-state row's HP over the profile's, so a player who
// takes damage and leaves (or is on the server when it shuts down) within a checkpoint interval
// comes back where the last checkpoint saw it, with the HP it had then. Pre-existing (identical on
// the wave-12 tip 91449a7), not a wave-13 regression.
public sealed class Wave13PlayerWorldStateOnLeaveTests(ITestOutputHelper output)
{
    private const string Anchor = "w13-ws-anchor";
    private const string Bob = "w13-ws-bob";

    // Measured unskipped: left at (6.5, 1.5, 11.5) with 83 HP; back at (6.5, 1.5, 7.0) with 100 HP.
    [Fact(Skip = "BUG (pre-existing): leaving discards the player's position and HP since the last checkpoint; see the class comment.")]
    public async Task LeaveAfterTheLastCheckpoint_ThenRejoin_KeepsThePositionAndHpTheyLeftWith()
    {
        using PersistenceTestDatabase database = new();
        await using Wave12DeltaActorLoopbackTests.Rig rig = await StartAsync(database);
        (Vector3 position, int health) = await PlayAfterACheckpointAsync(rig, database);

        await rig.DisconnectAsync(rig.Peers.Single(peer => peer.SessionId == Bob));
        Assert.True(rig.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        await rig.JoinAsync(Bob);
        ActorSnapshot back = BobOf(rig.Runtime);
        output.WriteLine($"left at {position} with {health} HP; back at {back.Position} with {back.CurrentHealth} HP");
        Assert.Equal(health, back.CurrentHealth);
        Assert.True((back.Position - position).Length < 0.05f, $"back at {back.Position}, left at {position}");
    }

    // Measured unskipped: shut down at (6.5, 1.5, 11.5) with 83 HP; back at (6.5, 1.5, 7.0) with 100 HP.
    [Fact(Skip = "BUG (pre-existing): the shutdown detaches every session before the final checkpoint; see the class comment.")]
    public async Task ShutdownAfterTheLastCheckpoint_ThenRestart_KeepsThePositionAndHpAtShutdown()
    {
        using PersistenceTestDatabase database = new();
        Vector3 position;
        int health;
        {
            await using Wave12DeltaActorLoopbackTests.Rig rig = await StartAsync(database);
            (position, health) = await PlayAfterACheckpointAsync(rig, database);
            // The production order: the service (and every connection) first, then the host.
            await rig.Service.DisposeAsync();
            rig.Host.Dispose();
        }

        SqliteConnection.ClearAllPools();
        {
            await using Wave12DeltaActorLoopbackTests.Rig rig = await StartAsync(database);
            await rig.JoinAsync(Anchor);
            await rig.JoinAsync(Bob);
            ActorSnapshot back = BobOf(rig.Runtime);
            output.WriteLine($"shut down at {position} with {health} HP; back at {back.Position} with {back.CurrentHealth} HP");
            Assert.Equal(health, back.CurrentHealth);
            Assert.True((back.Position - position).Length < 0.05f, $"back at {back.Position}, shut down at {position}");
        }
    }

    // Bob (a secondary seat) idles until a periodic checkpoint saved his world state, then walks
    // and is hit by an enemy. Returns his position and HP after that.
    private async Task<(Vector3 Position, int Health)> PlayAfterACheckpointAsync(Wave12DeltaActorLoopbackTests.Rig rig, PersistenceTestDatabase database)
    {
        WorldSessionRuntime runtime = rig.Runtime;
        await rig.JoinAsync(Anchor);
        Wave12DeltaActorLoopbackTests.Peer bob = await rig.JoinAsync(Bob);
        string worldStateRows = $"SELECT COUNT(*) FROM player_world_state WHERE player_name = 'Name {Bob}';";
        for (int i = 0; i < 1000 && database.Count(worldStateRows) == 0; i++)
        {
            await rig.StepAsync();
            if (i % 64 == 63)
            {
                await rig.DrainAllAsync();
            }
        }

        Assert.Equal(1, database.Count(worldStateRows));
        ActorSnapshot atCheckpoint = BobOf(runtime);

        // A hard-hitting stationary enemy beside Bob; it is removed once Bob has lost some HP.
        Vector3 enemyPosition = atCheckpoint.Position + new Vector3(1f, -0.5f, 0f);
        EnemyActor brute = new(
            new EnemyCombatant(() => enemyPosition, eventHub: null, actorId: 4500, maxHealth: 1000, attackDamage: 17, attackIntervalSeconds: 0.25f),
            enemyPosition,
            aggroRange: 6f,
            attackRange: 4f,
            moveSpeed: 0f,
            displayName: "Wave 13 brute");
        runtime.Enemies.Add(brute);
        for (int i = 0; i < 256 && BobOf(runtime).CurrentHealth == atCheckpoint.CurrentHealth; i++)
        {
            await rig.StepAsync();
        }

        brute.Combatant.ApplyDamage(1_000_000);
        runtime.Enemies.PruneInactive();
        Assert.True(BobOf(runtime).CurrentHealth < atCheckpoint.CurrentHealth, "the brute never hit Bob");

        // Then Bob walks for a second.
        for (int i = 0; i < 64; i++)
        {
            await bob.SendCommandAsync(walking: true, yaw: 0f);
            await rig.StepAsync();
        }

        await bob.SendCommandAsync(walking: false, yaw: 0f);
        await rig.StepAsync();
        await rig.DrainAllAsync();
        ActorSnapshot left = BobOf(runtime);
        output.WriteLine($"checkpoint saw {atCheckpoint.Position} with {atCheckpoint.CurrentHealth} HP; now {left.Position} with {left.CurrentHealth} HP");
        Assert.True((left.Position - atCheckpoint.Position).Length > 0.5f, "Bob did not move");
        Assert.True(left.IsAlive);
        return (left.Position, left.CurrentHealth);
    }

    private static ActorSnapshot BobOf(WorldSessionRuntime runtime) => runtime.CaptureActorSnapshots().Single(actor => actor.SessionId == Bob);

    private static Task<Wave12DeltaActorLoopbackTests.Rig> StartAsync(PersistenceTestDatabase database)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, database.SchemaPath));
        return Wave12DeltaActorLoopbackTests.Rig.StartAsync(new NullOutput(), recordExpected: false, host: host);
    }

    private sealed class NullOutput : ITestOutputHelper
    {
        public void WriteLine(string message)
        {
        }

        public void WriteLine(string format, params object[] args)
        {
        }
    }
}
