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
// (player_world_state: position, facing and current HP) was written only by checkpoints, and only
// for players that are still attached. DetachRemoteSession saved the profile (level, XP, profile
// HP) but not the world state, and the server's shutdown (DedicatedServerService dispose, then the
// host's final checkpoint) detaches every session before that final checkpoint runs. On the next
// join the world-state row's HP is preferred over the profile's, so a player who took damage and
// left (or was on the server when it shut down) within a checkpoint interval came back where the
// last checkpoint saw it, with the HP it had then (or, with no checkpoint yet, at the spawn with
// the profile's HP). Pre-existing (identical on the wave-12 tip 91449a7). Fixed in the wave 13 fix
// loop: DetachRemoteSession writes the world state (RuntimePersistenceBridge.PersistPlayerWorldState).
public sealed class Wave13PlayerWorldStateOnLeaveTests(ITestOutputHelper output)
{
    private const string Anchor = "w13-ws-anchor";
    private const string Bob = "w13-ws-bob";

    // Before the fix: left at (6.5, 1.5, 11.5) with 83 HP; back at (6.5, 1.5, 7.0) with 100 HP.
    [Fact]
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

    // Before the fix: shut down at (6.5, 1.5, 11.5) with 83 HP; back at (6.5, 1.5, 7.0) with 100 HP.
    [Fact]
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

    // A player who leaves before the first periodic checkpoint (5 s) has no world-state row yet;
    // the leave itself must write it.
    [Fact]
    public async Task LeaveBeforeTheFirstCheckpoint_ThenRejoin_KeepsThePositionAndHpTheyLeftWith()
    {
        using PersistenceTestDatabase database = new();
        await using Wave12DeltaActorLoopbackTests.Rig rig = await StartAsync(database);
        ulong joinTick = rig.Tick;
        (Vector3 position, int health) = await PlayAsync(rig, database, waitForCheckpoint: false);
        Assert.True(
            rig.Tick - joinTick < 5 * SimulationClock.AuthoritativeTicksPerSecond,
            $"{rig.Tick - joinTick} ticks played; the run must stay within the first checkpoint interval");
        Assert.True(rig.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        Assert.Equal(0, database.Count(WorldStateRows));

        await rig.DisconnectAsync(rig.Peers.Single(peer => peer.SessionId == Bob));
        Assert.True(rig.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        Assert.Equal(1, database.Count(WorldStateRows));
        Assert.Equal(health, database.Count($"SELECT current_hp FROM player_world_state WHERE player_name = 'Name {Bob}';"));
        Assert.Equal(health, database.Count($"SELECT current_hp FROM player_profiles WHERE player_name = 'Name {Bob}';"));

        await rig.JoinAsync(Bob);
        ActorSnapshot back = BobOf(rig.Runtime);
        output.WriteLine($"left at {position} with {health} HP; back at {back.Position} with {back.CurrentHealth} HP");
        Assert.Equal(health, back.CurrentHealth);
        Assert.True((back.Position - position).Length < 0.05f, $"back at {back.Position}, left at {position}");
    }

    private static readonly string WorldStateRows = $"SELECT COUNT(*) FROM player_world_state WHERE player_name = 'Name {Bob}';";

    private Task<(Vector3 Position, int Health)> PlayAfterACheckpointAsync(Wave12DeltaActorLoopbackTests.Rig rig, PersistenceTestDatabase database) =>
        PlayAsync(rig, database, waitForCheckpoint: true);

    // Bob (a secondary seat) idles until a periodic checkpoint saved his world state (when asked),
    // then is hit by an enemy and walks. Returns his position and HP after that.
    private async Task<(Vector3 Position, int Health)> PlayAsync(Wave12DeltaActorLoopbackTests.Rig rig, PersistenceTestDatabase database, bool waitForCheckpoint)
    {
        WorldSessionRuntime runtime = rig.Runtime;
        // The run counts ticks against the checkpoint interval, so a slow load must not play any.
        await rig.JoinAsync(Anchor, awaitPrefetchBeforeTicking: true);
        Wave12DeltaActorLoopbackTests.Peer bob = await rig.JoinAsync(Bob, awaitPrefetchBeforeTicking: true);
        string worldStateRows = WorldStateRows;
        for (int i = 0; waitForCheckpoint && i < 1000 && database.Count(worldStateRows) == 0; i++)
        {
            await rig.StepAsync();
            if (i % 64 == 63)
            {
                await rig.DrainAllAsync();
            }
        }

        Assert.Equal(waitForCheckpoint ? 1 : 0, database.Count(worldStateRows));
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
