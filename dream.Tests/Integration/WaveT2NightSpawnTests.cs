using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave T2e: the world clock is a gameplay input. At night the authoritative population step keeps a
// second wolf (at the night spawn) and removes it at dawn. It must be deterministic from the
// persisted ticks, TicksPerDay and the tick count, survive restarts without a third wolf, and never
// run on a client.
public sealed class WaveT2NightSpawnTests(ITestOutputHelper output)
{
    private static readonly WorldClockSettings OneMinuteDay = WorldClockSettings.FromDayLengthMinutes(1);

    [Fact]
    public void NightSpawn_IsNearTheFarCorner_ClampedAndApartFromTheDaySpawn()
    {
        Assert.Equal(new Vector3(52.5f, 1f, 52.5f), WolfSpawnPolicy.ResolveInitialSpawn(56, 56));
        Assert.Equal(new Vector3(53.5f, 1f, 53.5f), WolfSpawnPolicy.ResolveNightSpawn(56, 56));

        // 3 x 3: both inset corners clamp onto the same cell, so the night spawn steps away.
        Vector3 day = WolfSpawnPolicy.ResolveInitialSpawn(3, 3);
        Vector3 night = WolfSpawnPolicy.ResolveNightSpawn(3, 3);
        Assert.Equal(new Vector3(0.5f, 1f, 0.5f), day);
        Assert.Equal(new Vector3(1.5f, 1f, 0.5f), night);

        for (int width = 1; width <= 64; width++)
        {
            for (int depth = 1; depth <= 64; depth += 7)
            {
                Vector3 spawn = WolfSpawnPolicy.ResolveNightSpawn(width, depth);
                Assert.Equal(spawn, WolfSpawnPolicy.ResolveNightSpawn(width, depth));
                Assert.InRange(spawn.X, 0.5f, Math.Max(0.5f, width - 1.5f));
                Assert.InRange(spawn.Z, 0.5f, Math.Max(0.5f, depth - 1.5f));
                Assert.Equal(WolfSpawnPolicy.FloorSurfaceY, spawn.Y);
                if (width >= 4 || depth >= 4)
                {
                    Vector3 daySpawn = WolfSpawnPolicy.ResolveInitialSpawn(width, depth);
                    Assert.True((spawn.Xz - daySpawn.Xz).Length >= 1f, $"{width}x{depth}: night {spawn} on day {daySpawn}");
                }
            }
        }
    }

    [Fact]
    public void Registry_Remove_KeepsTheOthersInOrder()
    {
        EnemyRegistry registry = new();
        foreach (int id in new[] { 4300, 4302, 4301 })
        {
            registry.Add(CreateEnemy(id));
        }

        Assert.True(registry.Remove(4302));
        Assert.False(registry.Remove(4302));
        Assert.Equal([4300, 4301], registry.ActiveActors.Select(actor => actor.Combatant.Actor.Id).ToArray());
        AllocationAssert.AtMost(0, () => registry.Remove(9999));
    }

    // An enemy added from outside with an id the runtime never handed out (a test's scripted target)
    // counts as active, so by day it suppresses nothing new and at night it stands in for the night
    // wolf, but the dawn step never removes it: only the runtime's own wolves are despawned.
    [Fact]
    public void AnEnemyTheRuntimeDidNotSpawn_IsNeverDespawned()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        int wolf = Assert.Single(runtime.Enemies.ActiveActors).Combatant.Actor.Id;
        runtime.Enemies.Add(CreateEnemy(4400));
        for (ulong tick = 1; tick <= 32; tick++)
        {
            Tick(runtime, tick);
        }

        Assert.Equal(WorldDayPhase.Day, runtime.WorldClock.Phase);
        Assert.Equal([wolf, 4400], ActiveWolfIds(runtime));
    }

    // Same world, same tick count, one clock in Day and one in Night: the first tick already differs,
    // and only by the night wolf (one more enemy actor, at the night spawn, with the next enemy id).
    [Fact]
    public void DayAndNightServers_DivergeOnTheFirstTick_ByExactlyOneWolf()
    {
        using WorldSessionRuntime day = CreateRuntime();
        using WorldSessionRuntime night = CreateRuntime();
        night.ConfigureWorldClock(WorldClockSettings.Default, WorldTicksAt(WorldClockSettings.Default, dayIndex: 0, hour: 23));
        Assert.Equal(WorldDayPhase.Day, day.WorldClock.Phase);
        Assert.Equal(WorldDayPhase.Night, night.WorldClock.Phase);
        Assert.Equal(SimulationStateChecksum.Compute(day), SimulationStateChecksum.Compute(night));
        int dayWolf = Assert.Single(day.Enemies.ActiveActors).Combatant.Actor.Id;

        Tick(day, 1);
        Tick(night, 1);

        SimulationStateChecksum.Parts dayParts = SimulationStateChecksum.ComputeParts(day);
        SimulationStateChecksum.Parts nightParts = SimulationStateChecksum.ComputeParts(night);
        Assert.NotEqual(dayParts.Actors, nightParts.Actors);
        Assert.Equal(dayParts with { Actors = nightParts.Actors }, nightParts);

        Assert.Single(day.Enemies.ActiveActors);
        Assert.Equal(2, night.Enemies.ActiveActors.Count);
        EnemyActor nightWolf = night.Enemies.ActiveActors[1];
        Assert.Equal(dayWolf + 1, nightWolf.Combatant.Actor.Id);
        Assert.Equal(WolfSpawnPolicy.ResolveNightSpawn(night.Map.Width, night.Map.Depth), nightWolf.Position);

        IReadOnlyList<ActorSnapshot> dayActors = day.CaptureActorSnapshots();
        IReadOnlyList<ActorSnapshot> nightActors = night.CaptureActorSnapshots();
        Assert.Equal(dayActors.Count + 1, nightActors.Count);
        Assert.Equal(dayActors, nightActors.Where(actor => actor.ActorId != nightWolf.Combatant.Actor.Id).ToList());
    }

    // Two servers on equal clocks (1-minute day) run from shortly before 20:00 to after 04:00. Every
    // tick's checksum matches; the wolf is spawned on the first Night tick and removed on the first
    // Dawn tick, on both servers, and nowhere else.
    [Fact]
    public void TwoEqualNightClocks_AcrossNightfallAndDawn_AreIdenticalEveryTick()
    {
        long nightfall = WorldTicksAt(OneMinuteDay, dayIndex: 0, hour: 20);
        long dawn = WorldTicksAt(OneMinuteDay, dayIndex: 1, hour: 4);
        long start = nightfall - 50;
        int ticks = (int)(dawn - start + 50);

        using WorldSessionRuntime first = CreateRuntime();
        using WorldSessionRuntime second = CreateRuntime();
        first.ConfigureWorldClock(OneMinuteDay, start);
        second.ConfigureWorldClock(OneMinuteDay, start);

        List<(ulong Tick, int Wolves)> firstChanges = [];
        List<(ulong Tick, int Wolves)> secondChanges = [];
        int firstWolves = first.Enemies.ActiveActors.Count;
        int secondWolves = second.Enemies.ActiveActors.Count;
        for (ulong tick = 1; tick <= (ulong)ticks; tick++)
        {
            Tick(first, tick);
            Tick(second, tick);
            Assert.True(
                SimulationStateChecksum.Compute(first) == SimulationStateChecksum.Compute(second),
                $"diverged at tick {tick}: {SimulationStateChecksum.ComputeParts(first)} vs {SimulationStateChecksum.ComputeParts(second)}");
            Assert.Equal(first.WorldClock.Phase == WorldDayPhase.Night ? 2 : 1, first.Enemies.ActiveActors.Count);
            Track(first, tick, ref firstWolves, firstChanges);
            Track(second, tick, ref secondWolves, secondChanges);
        }

        ulong spawnTick = (ulong)(nightfall - start);
        ulong despawnTick = (ulong)(dawn - start);
        output.WriteLine($"{ticks} ticks; changes {string.Join(", ", firstChanges)}");
        Assert.Equal([(spawnTick, 2), (despawnTick, 1)], firstChanges);
        Assert.Equal(firstChanges, secondChanges);
        // The day wolf stays; the night wolf (the higher id) is the one removed.
        Assert.Equal(WorldSessionRuntime.FirstEnemyActorId, Assert.Single(first.Enemies.ActiveActors).Combatant.Actor.Id);
    }

    // A persistent server restarted in the middle of the night restores its two wolves (same ids)
    // and spawns no third, in memory or in the database.
    [Fact]
    public void RestartInTheMiddleOfTheNight_RestoresTwoWolves_AndSpawnsNoThird()
    {
        using TempDatabase database = new();
        int[] wolves = CreateNightWorldWithTwoWolves(database);

        SqlitePools.Release(database.DatabasePath);
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        using (DedicatedServerHost host = OpenHost(bundle, out WorldSessionRuntime runtime))
        {
            Assert.Equal(WorldDayPhase.Night, runtime.WorldClock.Phase);
            Assert.Equal(wolves, ActiveWolfIds(runtime));
            for (int i = 0; i < 128; i++)
            {
                AdvanceOneTick(host);
                Assert.Equal(wolves, ActiveWolfIds(runtime));
            }

            Assert.True(runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
            Assert.Equal(2, database.Count("enemy_instances"));
        }

        SqlitePools.Release(database.DatabasePath);
        Assert.Equal(2, database.Count("enemy_instances"));
    }

    // A crash after the dawn despawn but before its row delete (or any checkpoint) landed leaves two
    // wolf rows and a clock already at dawn. The restart restores both, and the first tick removes the
    // night wolf (the higher id) and its row.
    [Fact]
    public void CrashAfterTheDawnDespawn_RestoresTwoWolves_AndTheFirstTickRemovesOne()
    {
        using TempDatabase database = new();
        int[] wolves = CreateNightWorldWithTwoWolves(database);
        long dawn = WorldTicksAt(WorldClockSettings.Default, dayIndex: 2, hour: 4) + 3;
        SqlitePools.Release(database.DatabasePath);
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            new SqlitePersistenceCheckpointWriter(bundle.Schema).Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = dawn });
        }

        SqlitePools.Release(database.DatabasePath);
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        using (DedicatedServerHost host = OpenHost(bundle, out WorldSessionRuntime runtime))
        {
            Assert.Equal(WorldDayPhase.Dawn, runtime.WorldClock.Phase);
            Assert.Equal(wolves, ActiveWolfIds(runtime));
            AdvanceOneTick(host);
            Assert.Equal([wolves[0]], ActiveWolfIds(runtime));
            for (int i = 0; i < 64; i++)
            {
                AdvanceOneTick(host);
            }

            Assert.Equal([wolves[0]], ActiveWolfIds(runtime));
            Assert.True(runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
            Assert.Equal(1, database.Count("enemy_instances"));
        }

        SqlitePools.Release(database.DatabasePath);
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        using (DedicatedServerHost host = OpenHost(bundle, out WorldSessionRuntime runtime))
        {
            Assert.Equal([wolves[0]], ActiveWolfIds(runtime));
        }
    }

    // The client never spawns or removes enemies: its world (ClientWorldSession) has no clock and no
    // population step, so it only shows the replicated wolves however long it predicts. An
    // authoritative tick without gameplay simulation does not run the step either.
    [Fact]
    public void TheClient_NeverRunsTheNightStep()
    {
        Assert.DoesNotContain(
            typeof(ClientWorldSession).GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            member => member switch
            {
                FieldInfo field => field.FieldType == typeof(WorldClock),
                PropertyInfo property => property.PropertyType == typeof(WorldClock),
                _ => false
            });

        using WorldSessionRuntime server = CreateRuntime();
        server.ConfigureWorldClock(WorldClockSettings.Default, WorldTicksAt(WorldClockSettings.Default, dayIndex: 0, hour: 23));
        Tick(server, 1);
        Assert.Equal(2, server.Enemies.ActiveActors.Count);
        int[] replicated = ActiveWolfIds(server);

        using ClientWorldSession empty = new(WorldDefaults.StartupFloorSize, new Vector3(40f, 1.5f, 40f), new SceneEventHub());
        using ClientWorldSession client = new(WorldDefaults.StartupFloorSize, new Vector3(40f, 1.5f, 40f), new SceneEventHub());
        RemoteEnemyStateApplier.ApplyActorSnapshots(client.Enemies, server.CaptureActorSnapshots());
        Assert.Equal(replicated, client.Enemies.ActiveActors.Select(actor => actor.Combatant.Actor.Id).ToArray());
        for (ulong tick = 1; tick <= 256; tick++)
        {
            PlayerCommandFrame idle = new(
                PlayerSessionId.LocalDefault,
                tick,
                PlayerModeSwitchIntent.None,
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false);
            empty.AdvancePredictionTick(tick, SimulationClock.FixedDeltaSeconds, idle, PlayerSimulationTuning.AuthoritativeDefault);
            client.AdvancePredictionTick(tick, SimulationClock.FixedDeltaSeconds, idle, PlayerSimulationTuning.AuthoritativeDefault);
        }

        Assert.Empty(empty.Enemies.ActiveActors);
        Assert.Equal(replicated, client.Enemies.ActiveActors.Select(actor => actor.Combatant.Actor.Id).ToArray());

        using WorldSessionRuntime noGameplay = CreateRuntime();
        noGameplay.ConfigureWorldClock(WorldClockSettings.Default, WorldTicksAt(WorldClockSettings.Default, dayIndex: 0, hour: 23));
        for (ulong tick = 1; tick <= 64; tick++)
        {
            noGameplay.AdvanceAuthoritativeTick(
                tick,
                SimulationClock.FixedDeltaSeconds,
                default(PlayerCommandFrame),
                PlayerSimulationTuning.AuthoritativeDefault,
                allowGameplaySimulation: false);
        }

        Assert.Equal(WorldDayPhase.Night, noGameplay.WorldClock.Phase);
        Assert.Single(noGameplay.Enemies.ActiveActors);
    }

    // Wave I1 (I-D22): the night slot is the NightExtra template, the Dire Wolf.
    [Fact]
    public void ByNight_TheSecondEnemyIsADireWolf_AtTheNightSpawn_WithItsStats()
    {
        using WorldSessionRuntime night = CreateRuntime();
        night.ConfigureWorldClock(WorldClockSettings.Default, WorldTicksAt(WorldClockSettings.Default, dayIndex: 0, hour: 23));
        Tick(night, 1);

        Assert.Equal(["Wolf", "DireWolf"], Kinds(night));
        EnemyActor dire = night.Enemies.ActiveActors[1];
        Assert.Equal(WolfSpawnPolicy.ResolveNightSpawn(night.Map.Width, night.Map.Depth), dire.Position);
        Assert.Equal(200, dire.Combatant.Health.MaxHealth);
        Assert.Equal(200, dire.Combatant.Health.CurrentHealth);
        Assert.Equal(18, dire.Combatant.AttackDamage);
        Assert.Equal(1f, dire.Combatant.AttackIntervalSeconds);
        Assert.Equal(3, dire.Level);
        Assert.Equal(12f, dire.AggroRange);
        Assert.Equal("enemy.dire-wolf", dire.VisualKey);
        Assert.Equal(42, CombatFormulaService.Default.EvaluateEnemyExperienceReward(dire.Level, 1));

        EnemyActor wolf = night.Enemies.ActiveActors[0];
        Assert.Equal(1, wolf.Level);
        Assert.Null(wolf.VisualKey);
        Assert.Equal(100, wolf.Combatant.Health.MaxHealth);
        Assert.Equal(12, wolf.Combatant.AttackDamage);
    }

    [Fact]
    public void ByDay_OnlyTheWolfExists_AndNoDireWolfIsSpawnedWithTheGoldenRunsClock()
    {
        using WorldSessionRuntime day = CreateRuntime();
        for (ulong tick = 1; tick <= 200; tick++)
        {
            Tick(day, tick);
            Assert.Equal(["Wolf"], Kinds(day));
        }
    }

    [Fact]
    public void AtNight_KillingTheWolfSpawnsAWolf_AndKillingTheDireSpawnsADire()
    {
        using WorldSessionRuntime night = CreateRuntime();
        night.ConfigureWorldClock(WorldClockSettings.Default, WorldTicksAt(WorldClockSettings.Default, dayIndex: 0, hour: 23));
        Tick(night, 1);
        Assert.Equal(["Wolf", "DireWolf"], Kinds(night));

        night.Enemies.ActiveActors[0].Combatant.Health.ApplyDamage(10_000);
        Tick(night, 2);
        Assert.Equal(["DireWolf", "Wolf"], Kinds(night));
        Assert.Equal(WolfSpawnPolicy.ResolveNightSpawn(night.Map.Width, night.Map.Depth), night.Enemies.ActiveActors[1].Position);

        night.Enemies.ActiveActors[0].Combatant.Health.ApplyDamage(10_000);
        Tick(night, 3);
        Assert.Equal(["Wolf", "DireWolf"], Kinds(night));
    }

    [Fact]
    public void Dawn_RemovesTheDireWolf_EvenWithAHigherIdWolf()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.ConfigureWorldClock(OneMinuteDay, WorldTicksAt(OneMinuteDay, dayIndex: 0, hour: 23));
        Tick(runtime, 1);
        int dire = runtime.Enemies.ActiveActors[1].Combatant.Actor.Id;
        runtime.Enemies.ActiveActors[0].Combatant.Health.ApplyDamage(10_000);
        Tick(runtime, 2);
        Assert.Equal(["DireWolf", "Wolf"], Kinds(runtime));
        int wolf = runtime.Enemies.ActiveActors[1].Combatant.Actor.Id;
        Assert.True(wolf > dire);

        ulong tick = 3;
        while (tick < 3000)
        {
            Tick(runtime, tick++);
            if (runtime.WorldClock.Phase != WorldDayPhase.Night)
            {
                break;
            }

            Assert.Equal(["DireWolf", "Wolf"], Kinds(runtime));
        }

        Assert.Equal(WorldDayPhase.Dawn, runtime.WorldClock.Phase);
        Assert.Equal(["Wolf"], Kinds(runtime));
        Assert.Equal(wolf, Assert.Single(runtime.Enemies.ActiveActors).Combatant.Actor.Id);
    }

    // Wave I fix loop 1 (N2): a Dire that is the only enemy when the night ends is replaced by the day's Wolf.
    [Fact]
    public void ALoneDireWolfAtDawn_IsRemoved_AndTheNextTickSpawnsTheDaysWolf()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.ConfigureWorldClock(OneMinuteDay, WorldTicksAt(OneMinuteDay, dayIndex: 0, hour: 23));
        Tick(runtime, 1);
        Assert.Equal(["Wolf", "DireWolf"], Kinds(runtime));

        // Kill the Wolf before every tick, so the first tick of the dawn finds only the Dire.
        ulong tick = 2;
        for (; tick < 3000; tick++)
        {
            foreach (EnemyActor actor in runtime.Enemies.ActiveActors.Where(a => a.IsActive && a.EnemyKind == "Wolf").ToList())
            {
                actor.Combatant.Health.ApplyDamage(10_000);
            }

            Tick(runtime, tick);
            if (runtime.WorldClock.Phase != WorldDayPhase.Night)
            {
                break;
            }

            Assert.Contains("DireWolf", Kinds(runtime));
        }

        Assert.Equal(WorldDayPhase.Dawn, runtime.WorldClock.Phase);
        Assert.Empty(Kinds(runtime));
        Tick(runtime, tick + 1);
        Assert.Equal(["Wolf"], Kinds(runtime));
        for (ulong later = tick + 2; later < tick + 40; later++)
        {
            Tick(runtime, later);
            Assert.Equal(["Wolf"], Kinds(runtime));
        }
    }

    [Fact]
    public void TwoRuns_AcrossNightfallAndDawn_SpawnTheSameKindsAtTheSameTicks()
    {
        long nightfall = WorldTicksAt(OneMinuteDay, dayIndex: 0, hour: 20);
        long dawn = WorldTicksAt(OneMinuteDay, dayIndex: 1, hour: 4);
        long start = nightfall - 50;
        int ticks = (int)(dawn - start + 50);
        using WorldSessionRuntime first = CreateRuntime();
        using WorldSessionRuntime second = CreateRuntime();
        first.ConfigureWorldClock(OneMinuteDay, start);
        second.ConfigureWorldClock(OneMinuteDay, start);

        bool sawDire = false;
        for (ulong tick = 1; tick <= (ulong)ticks; tick++)
        {
            Tick(first, tick);
            Tick(second, tick);
            Assert.Equal(Kinds(first), Kinds(second));
            Assert.Equal(SimulationStateChecksum.Compute(first), SimulationStateChecksum.Compute(second));
            sawDire |= Kinds(first).Contains("DireWolf");
        }

        Assert.True(sawDire);
        Assert.Equal(["Wolf"], Kinds(first));
    }

    // A catalog with no NightExtra template behaves as before the Dire Wolf: wolves only, at the same ticks.
    [Fact]
    public void ACatalogWithoutANightExtra_SpawnsOnlyWolves_WithTheSamePopulationAsBefore()
    {
        ContentCatalog wolfOnly = ContentCatalog.Load(
            File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.ItemsFileName)),
            """
            {"version":2,"enemies":[{"key":"Wolf","displayName":"Wolf","level":1,"strength":10,"vitality":10,
             "attackSpeed":0.952381,"aggroRange":10,"attackRange":1.35,"moveSpeed":2.8,
             "visualKey":"enemy.wolf","spawnRole":"Primary"}]}
            """,
            File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.LootFileName)));
        Assert.DoesNotContain(wolfOnly.Enemies, template => template.SpawnRole == EnemySpawnRole.NightExtra);

        long nightfall = WorldTicksAt(OneMinuteDay, dayIndex: 0, hour: 20);
        long dawn = WorldTicksAt(OneMinuteDay, dayIndex: 1, hour: 4);
        long start = nightfall - 50;
        int ticks = (int)(dawn - start + 50);
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.EnemyContent = wolfOnly;
        runtime.ConfigureWorldClock(OneMinuteDay, start);

        List<(ulong Tick, int Wolves)> changes = [];
        int wolves = runtime.Enemies.ActiveActors.Count;
        for (ulong tick = 1; tick <= (ulong)ticks; tick++)
        {
            Tick(runtime, tick);
            Assert.All(Kinds(runtime), kind => Assert.Equal("Wolf", kind));
            Assert.Equal(runtime.WorldClock.Phase == WorldDayPhase.Night ? 2 : 1, runtime.Enemies.ActiveActors.Count);
            Track(runtime, tick, ref wolves, changes);
        }

        Assert.Equal([((ulong)(nightfall - start), 2), ((ulong)(dawn - start), 1)], changes);
    }

    private static string[] Kinds(WorldSessionRuntime runtime) =>
        runtime.Enemies.ActiveActors.Where(actor => actor.IsActive).Select(actor => actor.EnemyKind).ToArray();

    // The persisted world ticks at which the clock shows hour:00 on day dayIndex.
    internal static long WorldTicksAt(WorldClockSettings settings, long dayIndex, int hour)
    {
        long ofDay = (long)Math.Round(hour / 24d * settings.TicksPerDay);
        long ticks = (dayIndex * settings.TicksPerDay) + ofDay - settings.StartOffsetTicks;
        Assert.True(ticks >= 0, $"{hour}:00 on day {dayIndex} is before the world began");
        return ticks;
    }

    // A fresh persistent world at 23:00 (default day), ticked until the night wolf exists, then shut
    // down cleanly: two wolf rows and a night clock. Returns the wolves' ids in registry order.
    private int[] CreateNightWorldWithTwoWolves(TempDatabase database)
    {
        long night = WorldTicksAt(WorldClockSettings.Default, dayIndex: 1, hour: 23);
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            new SqlitePersistenceCheckpointWriter(bundle.Schema).Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = night });
        }

        SqlitePools.Release(database.DatabasePath);
        int[] wolves;
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        using (DedicatedServerHost host = OpenHost(bundle, out WorldSessionRuntime runtime))
        {
            Assert.Equal(WorldDayPhase.Night, runtime.WorldClock.Phase);
            Assert.Single(runtime.Enemies.ActiveActors);
            AdvanceOneTick(host);
            wolves = ActiveWolfIds(runtime);
            Assert.Equal(2, wolves.Length);
            for (int i = 0; i < 16; i++)
            {
                AdvanceOneTick(host);
            }

            Assert.Equal(wolves, ActiveWolfIds(runtime));
            Assert.True(runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        }

        SqlitePools.Release(database.DatabasePath);
        Assert.Equal(2, database.Count("enemy_instances"));
        output.WriteLine($"night world: wolves {string.Join(", ", wolves)}");
        return wolves;
    }

    // The production host with the default day pinned (DREAM_DAY_LENGTH_MINUTES may be set by a
    // test in another collection) at the bundle's stored world time.
    private static DedicatedServerHost OpenHost(ServerPersistenceBundle bundle, out WorldSessionRuntime runtime)
    {
        long worldTicks = bundle.WorldTimeTicks;
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle);
        runtime = host.Runtime!;
        runtime.ConfigureWorldClock(WorldClockSettings.Default, worldTicks);
        return host;
    }

    private static void AdvanceOneTick(DedicatedServerHost host)
    {
        int executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        if (executed == 0)
        {
            executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
        }

        Assert.Equal(1, executed);
    }

    private static int[] ActiveWolfIds(WorldSessionRuntime runtime) =>
        runtime.Enemies.ActiveActors.Where(actor => actor.IsActive).Select(actor => actor.Combatant.Actor.Id).ToArray();

    private static void Track(WorldSessionRuntime runtime, ulong tick, ref int wolves, List<(ulong Tick, int Wolves)> changes)
    {
        int count = runtime.Enemies.ActiveActors.Count;
        if (count != wolves)
        {
            changes.Add((tick, count));
            wolves = count;
        }
    }

    private static EnemyActor CreateEnemy(int actorId)
    {
        EnemyCombatant combatant = new(() => Vector3.Zero, eventHub: null, actorId: actorId, maxHealth: 10, attackDamage: 1, attackIntervalSeconds: 1f);
        return new EnemyActor(combatant, Vector3.Zero, aggroRange: 1f, attackRange: 1f, moveSpeed: 0f, displayName: "Wolf");
    }

    private static WorldSessionRuntime CreateRuntime()
    {
        WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"), "session-a");
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"), "session-b");
        return runtime;
    }

    private static void Tick(WorldSessionRuntime runtime, ulong tick)
    {
        runtime.AdvanceAuthoritativeTick(
            tick,
            SimulationClock.FixedDeltaSeconds,
            default(PlayerCommandFrame),
            PlayerSimulationTuning.AuthoritativeDefault);
    }
}
