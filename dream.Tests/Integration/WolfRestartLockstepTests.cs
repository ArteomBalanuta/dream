using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Integration;

// Wave I, last fix loop: two servers on their own databases run the same script, one wolf chasing and biting a
// player on edited terrain, and are restarted together mid-fight. The persisted-world lockstep scripts park or
// replace every wolf, so this is the one run where the real wolf's chase, bite, restore and leash are compared
// tick by tick across a restart.
public sealed class WolfRestartLockstepTests(ITestOutputHelper output)
{
    private const string Session = "restart-a";
    private const string Name = "Restart A";
    private const int BeforeRestart = 140;
    private const int AfterRestart = PlayerRespawnRules.SpawnProtectionTicks + 260;

    private sealed class Pair : IDisposable
    {
        private readonly WaveIRuntimeKit.RealRun[] _runs = new WaveIRuntimeKit.RealRun[2];
        private readonly IAuthoritativePlayerTarget[] _targets = new IAuthoritativePlayerTarget[2];
        public readonly int[] FirstBiteTick = [-1, -1];
        private readonly int[] _healthAtJoin = new int[2];
        public int Ticks;

        public Pair(MigrationTestDatabase first, MigrationTestDatabase second)
        {
            _runs[0] = new WaveIRuntimeKit.RealRun(first);
            _runs[1] = new WaveIRuntimeKit.RealRun(second);
        }

        public WorldSessionRuntime Runtime(int index) => _runs[index].Runtime;

        public void Join()
        {
            for (int i = 0; i < 2; i++)
            {
                _runs[i].Host.JoinSession(Session, Name);
                _targets[i] = WaveIRuntimeKit.Target(_runs[i].Runtime, Session);
                _healthAtJoin[i] = _targets[i].Combatant.Health.CurrentHealth;
            }
        }

        public IAuthoritativePlayerTarget Target(int index) => _targets[index];

        public void Both(Action<WorldSessionRuntime> action)
        {
            action(_runs[0].Runtime);
            action(_runs[1].Runtime);
        }

        // One tick on each, then the checksums must agree.
        public void Step()
        {
            _runs[0].Advance(1);
            _runs[1].Advance(1);
            Ticks++;
            ulong a = SimulationStateChecksum.Compute(_runs[0].Runtime);
            ulong b = SimulationStateChecksum.Compute(_runs[1].Runtime);
            Assert.True(a == b, $"tick {Ticks}: the two servers diverged ({SimulationStateChecksum.ComputeParts(_runs[0].Runtime)} vs {SimulationStateChecksum.ComputeParts(_runs[1].Runtime)})");
            for (int i = 0; i < 2; i++)
            {
                if (FirstBiteTick[i] < 0 && _targets[i].Combatant.Health.CurrentHealth < _healthAtJoin[i])
                {
                    FirstBiteTick[i] = Ticks;
                }
            }
        }

        public void Dispose()
        {
            _runs[0].Dispose();
            _runs[1].Dispose();
        }
    }

    private static void Edit(WorldSessionRuntime runtime)
    {
        Map map = runtime.Map;
        // A terrain edit: clear a pocket around the spawn (generated cubes removed, persisted as terrain
        // overrides), on both servers alike. The wolf walks through it.
        for (int x = 0; x <= 14; x++)
        {
            for (int y = 1; y <= 4; y++)
            {
                for (int z = 0; z <= 16; z++)
                {
                    Cube? cube = map.TryGetCubeAtCell(x, y, z);
                    if (cube != null)
                    {
                        Assert.True(map.RemoveCube(cube));
                    }
                }
            }
        }

    }

    [Fact]
    public void AWolfChasingAndBiting_StaysInLockstepAcrossARestartOfBothServers_OnEditedTerrain()
    {
        using MigrationTestDatabase db1 = new();
        using MigrationTestDatabase db2 = new();
        db1.Schema.EnsureSchema();
        db2.Schema.EnsureSchema();
        int[] firstBites = [-1, -1];
        Vector3[] wolfAtRestart = new Vector3[2];
        int[] healthAtRestart = new int[2];

        using (Pair pair = new(db1, db2))
        {
            pair.Join();
            pair.Both(Edit);
            pair.Both(runtime =>
            {
                WaveIRuntimeKit.EndProtection(runtime, new Dream.Multiplayer.Sessions.PlayerSessionId(Session));
                EnemyActor wolf = runtime.Enemies.ActiveActors.First(e => e.IsActive);
                // Six metres from the player.
                wolf.SetPosition(new Vector3(5.5f, 1f, 11f));
            });

            for (int i = 0; i < BeforeRestart; i++)
            {
                pair.Step();
            }

            for (int index = 0; index < 2; index++)
            {
                EnemyActor wolf = pair.Runtime(index).Enemies.ActiveActors.First(e => e.IsActive);
                wolfAtRestart[index] = wolf.Position;
                healthAtRestart[index] = pair.Target(index).Combatant.Health.CurrentHealth;
                Assert.Equal(EnemyBehaviorState.Attacking, wolf.BehaviorState);
                Assert.True(pair.Target(index).Combatant.Health.CurrentHealth < pair.Target(index).Combatant.Health.MaxHealth,
                    "the wolf had not bitten before the restart");
                firstBites[index] = pair.FirstBiteTick[index];
            }

            Assert.Equal(wolfAtRestart[0], wolfAtRestart[1]);
            Assert.Equal(healthAtRestart[0], healthAtRestart[1]);
            Assert.True(Horizontal(wolfAtRestart[0], pair.Target(0).Position) < 1.4f, "the wolf was not at the player");
            pair.Both(runtime => runtime.Host_DetachForTest(Session));
        }

        output.WriteLine($"before the restart: first bite at tick {firstBites[0]} / {firstBites[1]}, wolf {wolfAtRestart[0]}, health {healthAtRestart[0]}");

        // Restart both servers on the same databases.
        using (Pair pair = new(db1, db2))
        {
            pair.Join();
            for (int index = 0; index < 2; index++)
            {
                Map map = pair.Runtime(index).Map;
                Assert.Null(map.TryGetCubeAtCell(2, 1, 2));
                Assert.NotNull(map.TryGetCubeAtCell(20, 1, 20));
                EnemyActor restored = pair.Runtime(index).Enemies.ActiveActors.First(e => e.IsActive);
                // The restored wolf is where the final checkpoint left it, and its home is the day spawn.
                Assert.True(Horizontal(restored.Position, wolfAtRestart[index]) < 0.01f, $"the wolf was restored at {restored.Position}, not {wolfAtRestart[index]}");
                Assert.Equal(WolfSpawnPolicy.ResolveInitialSpawn(map.Width, map.Depth), restored.HomePosition);
                Assert.Equal(healthAtRestart[index], pair.Target(index).Combatant.Health.CurrentHealth);
            }

            for (int i = 0; i < AfterRestart; i++)
            {
                pair.Step();
            }

            for (int index = 0; index < 2; index++)
            {
                Assert.True(pair.FirstBiteTick[index] > PlayerRespawnRules.SpawnProtectionTicks,
                    $"server {index}: first bite after the restart at tick {pair.FirstBiteTick[index]}; the rejoin window is {PlayerRespawnRules.SpawnProtectionTicks}");
                Assert.True(pair.FirstBiteTick[index] <= PlayerRespawnRules.SpawnProtectionTicks + 120,
                    $"server {index}: the wolf did not bite soon after the window ended ({pair.FirstBiteTick[index]})");
            }

            output.WriteLine($"after the restart: first bite at tick {pair.FirstBiteTick[0]} / {pair.FirstBiteTick[1]} of {pair.Ticks}");
        }
    }

    private static float Horizontal(Vector3 a, Vector3 b)
    {
        Vector3 d = a - b;
        d.Y = 0f;
        return d.Length;
    }
}

internal static class RuntimeTestExtensions
{
    public static void Host_DetachForTest(this WorldSessionRuntime runtime, string session)
        => runtime.DetachRemoteSession(new Dream.Multiplayer.Sessions.PlayerSessionId(session));
}
