using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-AI-3: a night camp's dawn despawn skipped any member that had a damage contributor. Contributors
// are a lifetime damage ledger, not a combat flag - nothing clears one outside a session detach - so one
// non-fatal hit left a night-tier enemy standing through every following day while the camp refilled
// around it at the next dusk. The wait now ends after DawnDespawnPatienceTicks (3 s of simulation);
// an enemy actually chasing or attacking is still spared, as it always was.
public sealed class AiNightCampDawnDespawnTests
{
    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, fileName));

    private static ContentCatalog LoadCamps(string campsJson) => ContentCatalog.Load(
        Read(ContentCatalog.ItemsFileName),
        Read(ContentCatalog.EnemiesFileName),
        Read(ContentCatalog.LootFileName),
        campsJson);

    private static long WorldTicksAt(int hour, WorldClockSettings settings) =>
        settings.TicksPerDay + (long)Math.Round(hour / 24d * settings.TicksPerDay) - settings.StartOffsetTicks;

    private static WorldSessionRuntime CreateNightCampRuntime()
    {
        // One two-member night camp, far from the world spawn, on a one-minute day.
        ContentCatalog content = LoadCamps(
            "{\"version\":1,\"camps\":[{\"key\":\"dark\",\"template\":\"Wolf\",\"x\":30.5,\"z\":30.5,\"count\":2,\"respawnSeconds\":1,\"night\":true}]}");
        WorldClockSettings shortDay = WorldClockSettings.FromDayLengthMinutes(1);
        WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.WriteCheckpointOnDispose = false;
        runtime.Enemies.Clear();
        runtime.EnemyContent = content;
        runtime.ConfigureWorldClock(shortDay, WorldTicksAt(2, shortDay));
        return runtime;
    }

    private static void Tick(WorldSessionRuntime runtime, ref ulong tick, int count)
    {
        for (int i = 0; i < count; i++)
        {
            tick++;
            runtime.AdvanceAuthoritativeTick(
                tick,
                SimulationClock.FixedDeltaSeconds,
                default(PlayerCommandFrame),
                PlayerSimulationTuning.AuthoritativeDefault);
        }
    }

    private static int Active(WorldSessionRuntime runtime) =>
        runtime.Enemies.ActiveActors.Count(enemy => enemy.IsActive);

    [Fact]
    public void ADawnDespawn_Completes_EvenForAMemberThatTookOneNonFatalHit()
    {
        using WorldSessionRuntime runtime = CreateNightCampRuntime();
        ulong tick = 0;
        Tick(runtime, ref tick, 10);
        Assert.Equal(2, Active(runtime));

        EnemyActor fought = runtime.Enemies.ActiveActors.OrderBy(enemy => enemy.Combatant.Actor.Id).Last();
        fought.RecordDamage(new PlayerSessionId("fighter"), 5);
        Assert.False(fought.Contributors.IsEmpty, "the damage ledger still names the fighter");

        // Dawn: the undamaged member goes at once, and the damaged one is still given its grace.
        Tick(runtime, ref tick, 400);
        Assert.NotEqual(WorldDayPhase.Night, runtime.WorldClock.Phase);
        Assert.Equal(1, Active(runtime));
        Assert.Contains(runtime.Enemies.ActiveActors, enemy => ReferenceEquals(enemy, fought) && enemy.IsActive);

        // Past the patience the ledger no longer holds it: the camp finishes despawning on its own, with
        // nothing clearing the contributor (production only clears one when the session detaches).
        Tick(runtime, ref tick, 400);
        Assert.NotEqual(WorldDayPhase.Night, runtime.WorldClock.Phase);
        Assert.Equal(0, Active(runtime));
        Assert.False(fought.Contributors.IsEmpty, "the damage ledger still names the fighter");
    }
}
