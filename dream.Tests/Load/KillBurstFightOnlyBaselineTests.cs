using System.Diagnostics;
using System.Reflection;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Characters;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Load;

// Wave I tester: the plan's "C0b fight-only p99" reference. The same 100 real kills at 5 a second across
// 10 characters as KillBurstMeasurementTests, with no loot, written against members that exist at wave I C0
// (de4c374) and at the tip alike (the private target lookup is reached by reflection), so one file runs on
// both commits. At the tip loot is switched off by giving the runtime tables that always roll nothing (the roll still runs).
// Release, DREAM_LOAD_TEST=1; the numbers are for comparison, so it asserts only that every kill happened.
[Collection(TimingSensitiveCollection.Name)]
public sealed class KillBurstFightOnlyBaselineTests(ITestOutputHelper output)
{
    private const int Kills = 100;
    private const int Characters = 10;
    private const int TicksPerKill = SimulationClock.AuthoritativeTicksPerSecond / 5;
    private const double TickMilliseconds = 1000d / SimulationClock.AuthoritativeTicksPerSecond;

    // Tables that roll "nothing" (all but one in a million): the kill path runs the roll, and no item, write or push follows.
    private const string NothingTable =
        "{\"version\":1,\"tables\":[" +
        "{\"key\":\"wolf\",\"drops\":1,\"nothingWeight\":1000000,\"items\":[{\"item\":\"gear.leather-cap\",\"weight\":1}],\"rarity\":{\"common\":1,\"uncommon\":0,\"rare\":0}}," +
        "{\"key\":\"dire-wolf\",\"drops\":1,\"nothingWeight\":1000000,\"items\":[{\"item\":\"gear.leather-cap\",\"weight\":1}],\"rarity\":{\"common\":1,\"uncommon\":0,\"rare\":0}}," +
        "{\"key\":\"ashen-prowler\",\"drops\":1,\"nothingWeight\":1000000,\"items\":[{\"item\":\"gear.leather-cap\",\"weight\":1}],\"rarity\":{\"common\":1,\"uncommon\":0,\"rare\":0}}," +
        "{\"key\":\"grave-howler\",\"drops\":1,\"nothingWeight\":1000000,\"items\":[{\"item\":\"gear.leather-cap\",\"weight\":1}],\"rarity\":{\"common\":1,\"uncommon\":0,\"rare\":0}}]}";

    private static IAuthoritativePlayerTarget Target(WorldSessionRuntime runtime, PlayerSessionId session)
    {
        MethodInfo method = typeof(WorldSessionRuntime).GetMethod(
            "TryResolveAuthoritativePlayerTarget",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        object?[] args = [session, null, null];
        Assert.True((bool)method.Invoke(runtime, args)!);
        return (IAuthoritativePlayerTarget)args[2]!;
    }

    private static void Advance(WorldSessionRuntime runtime, ref ulong tick, int count)
    {
        for (int i = 0; i < count; i++)
        {
            runtime.AdvanceAuthoritativeTick(++tick, SimulationClock.FixedDeltaSeconds, default(PlayerCommandFrame), PlayerSimulationTuning.AuthoritativeDefault);
        }
    }

    [LoadFact]
    public void Kills100_WithNoLoot_TickTimes()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        List<double> ticks = [];
        List<double> killTicks = [];
        using (RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)),
            null))
        {
            WorldSessionRuntime runtime = host.Runtime;
            PropertyInfo? loot = typeof(WorldSessionRuntime).GetProperty("LootContent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (loot != null)
            {
                string dir = Dream.Simulation.Content.ContentCatalog.ShippedDirectory;
                loot.SetValue(runtime, Dream.Simulation.Content.ContentCatalog.Load(
                    File.ReadAllText(Path.Combine(dir, Dream.Simulation.Content.ContentCatalog.ItemsFileName)),
                    File.ReadAllText(Path.Combine(dir, Dream.Simulation.Content.ContentCatalog.EnemiesFileName)),
                    NothingTable));
            }

            PlayerSessionId[] sessions = [.. Enumerable.Range(0, Characters).Select(i => new PlayerSessionId($"burst-{i}"))];
            foreach (PlayerSessionId session in sessions)
            {
                runtime.AttachRemoteSession(session, session.Value, new CharacterKey(session.Value));
                runtime.RestorePersistedPlayerState(session, session.Value, session.Value, account: null);
            }

            ulong tick = 0;
            Advance(runtime, ref tick, 3);
            foreach (PlayerSessionId session in sessions)
            {
                if (runtime.PrimaryRemoteSessionId == session)
                {
                    runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
                }
                else
                {
                    runtime.AdvanceSecondaryRemoteSession(
                        new PlayerCommandFrame(session, ulong.MaxValue / 2, new PlayerModeSwitchIntent(PlayerInteractionMode.Combat), PlayerMoveIntent.None, PlayerLookIntent.None, PrimaryUsePressed: false),
                        SimulationClock.FixedDeltaSeconds,
                        PlayerSimulationTuning.AuthoritativeDefault);
                }
            }

            Advance(runtime, ref tick, TicksPerKill);
            ulong sequence = 1UL << 40;
            int kills = 0;
            long nextTick = Stopwatch.GetTimestamp();
            long period = (long)(Stopwatch.Frequency * TickMilliseconds / 1000d);
            for (int i = 0; kills < Kills; i++)
            {
                while (Stopwatch.GetTimestamp() < nextTick)
                {
                    Thread.SpinWait(64);
                }

                nextTick += period;
                long start = Stopwatch.GetTimestamp();
                bool killTick = i % TicksPerKill == 0 && runtime.Enemies.ActiveActors.Count > 0;
                if (killTick)
                {
                    PlayerSessionId killer = sessions[kills % Characters];
                    IAuthoritativePlayerTarget target = Target(runtime, killer);
                    target.Combatant.Health.SetAuthoritativeState(target.Combatant.Health.MaxHealth, target.Combatant.Health.MaxHealth);
                    EnemyActor wolf = runtime.Enemies.ActiveActors.First();
                    wolf.SetPosition(target.EyePosition + target.Facing * 1f - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f));
                    wolf.Combatant.Health.SetAuthoritativeState(1, wolf.Combatant.Health.MaxHealth);
                    AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(
                        new PlayerActionMessage(killer, ++sequence, new CombatUseIntent(target.EyePosition, target.Facing)));
                    Assert.True(result.Payload is CombatResolutionResult { TargetDied: true }, $"kill {kills}: {result.Rejection?.Reason}");
                    kills++;
                }

                Advance(runtime, ref tick, 1);
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                ticks.Add(elapsed);
                if (killTick)
                {
                    killTicks.Add(elapsed);
                }
            }

            Assert.True(runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        }

        output.WriteLine(
            $"fight only, {Kills} kills: tick p50 {Percentile(ticks, 0.5):0.00} ms, p99 {Percentile(ticks, 0.99):0.00} ms, max {ticks.Max():0.00} ms; " +
            $"kill tick p50 {Percentile(killTicks, 0.5):0.00} ms, p99 {Percentile(killTicks, 0.99):0.00} ms, max {killTicks.Max():0.00} ms");
    }

    private static double Percentile(List<double> values, double percentile)
    {
        double[] sorted = [.. values.Order()];
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * percentile) - 1)];
    }
}
