using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Integration;

namespace Dream.Tests.Simulation.Runtime;

// The idle spawn loop of wave I: a new player who joins at the world spawn and stands still was bitten 9 ticks
// in and killed after 8.6 s, then looped through respawns, because the day wolf spawned 2.5 m away and stayed
// there. Join protection, a spawn outside aggro range and the leash home fix it; this measures it on a fresh
// world by day and by night, on the maps the game uses.
public sealed class IdleNewPlayerTests(ITestOutputHelper output)
{
    private const int HundredSeconds = 100 * SimulationClock.AuthoritativeTicksPerSecond;
    private static readonly PlayerSessionId A = new("idle-a");

    [Theory]
    [InlineData(56, false)]
    [InlineData(56, true)]
    [InlineData(30, false)]
    [InlineData(30, true)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    public void ANewPlayerWhoStandsStill_IsNotAttackedIn100Seconds_ByDayOrByNight(int floorSize, bool night)
    {
        using WorldSessionRuntime runtime = new(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        if (night)
        {
            WorldClockSettings settings = WorldClockSettings.Default;
            runtime.ConfigureWorldClock(settings, WaveT2NightSpawnTests.WorldTicksAt(settings, dayIndex: 1, hour: 22));
        }

        runtime.AttachRemoteSession(A, A.Value);
        IAuthoritativePlayerTarget player = WaveIRuntimeKit.Target(runtime, A.Value);
        int max = player.Combatant.Health.MaxHealth;
        ulong tick = 0;
        float closest = float.MaxValue;
        int attackingTicks = 0;
        int chasingTicks = 0;
        int wolvesSeen = 0;
        for (int i = 0; i < HundredSeconds; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            int active = 0;
            foreach (EnemyActor wolf in runtime.Enemies.ActiveActors)
            {
                if (!wolf.IsActive)
                {
                    continue;
                }

                active++;
                Vector3 d = wolf.Position - player.Position;
                d.Y = 0f;
                closest = Math.Min(closest, d.Length);
                attackingTicks += wolf.BehaviorState == EnemyBehaviorState.Attacking ? 1 : 0;
                chasingTicks += wolf.BehaviorState == EnemyBehaviorState.Chasing ? 1 : 0;
            }

            wolvesSeen = Math.Max(wolvesSeen, active);
            Assert.Equal(max, player.Combatant.Health.CurrentHealth);
        }

        output.WriteLine(
            $"map {floorSize}, {(night ? "night" : "day")}: {HundredSeconds} ticks, health {player.Combatant.Health.CurrentHealth}/{max}, " +
            $"wolves {wolvesSeen}, closest wolf {closest:F1} m, attacking ticks {attackingTicks}, chasing ticks {chasingTicks}");
        Assert.Equal(0, attackingTicks);
        Assert.Equal(0, chasingTicks);
        Assert.True(wolvesSeen >= (night ? 2 : 1), "the scenario had no wolves");
        Assert.True(closest > 12f, $"a wolf came within {closest} m");
    }
}
