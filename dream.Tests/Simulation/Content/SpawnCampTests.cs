using System.Reflection;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Content;

// M2-B: spawn camps. spawns.json is validated at load; a camp fills on the first tick, a dead member is
// replaced after respawnSeconds counted in ticks, a night camp exists only at night, and a restart keeps
// every camp member with its camp (camp_key) instead of spawning a second set.
public sealed class SpawnCampTests
{
    private const int TicksPerSecond = SimulationClock.AuthoritativeTicksPerSecond;

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, fileName));

    private static ContentCatalog Load(string spawnsJson) =>
        ContentCatalog.Load(Read(ContentCatalog.ItemsFileName), Read(ContentCatalog.EnemiesFileName), Read(ContentCatalog.LootFileName), spawnsJson);

    // What a 56 m world holds by day once every camp has filled: the authored counts of the camps that are not
    // night-only. Derived, so a camp added to spawns.json moves these tests with it; the literal totals are
    // pinned once, in TheShippedCamps_AreNineDayCampsAndThreeNightCamps_OfWolvesAndDireWolves.
    private static readonly int ShippedDayPopulation = ContentCatalog.Shipped.Camps
        .Where(camp => camp.ExistsOn(WorldDefaults.StartupFloorSize) && !camp.Night)
        .Sum(camp => camp.Count);

    private static string Camps(params string[] camps) => "{\"version\":1,\"camps\":[" + string.Join(",", camps) + "]}";

    private static string Camp(string key = "camp-a", string template = "Wolf", string x = "8.5", string z = "40.5", string count = "2", string respawn = "1", string extra = "") =>
        $"{{\"key\":\"{key}\",\"template\":\"{template}\",\"x\":{x},\"z\":{z},\"count\":{count},\"respawnSeconds\":{respawn}{extra}}}";

    // ---- content ----

    [Fact]
    public void TheShippedCamps_AreNineDayCampsAndThreeNightCamps_OfWolvesAndDireWolves()
    {
        // The camps of a 56 m world; the frontier camps (M9) exist on 100 m worlds only.
        IReadOnlyList<SpawnCamp> camps = [.. ContentCatalog.Shipped.Camps.Where(camp => camp.ExistsOn(WorldDefaults.StartupFloorSize))];

        Assert.Equal(12, camps.Count);
        Assert.Equal(9, camps.Count(camp => !camp.Night));
        Assert.Equal(3, camps.Count(camp => camp.Night));
        Assert.Contains(camps, camp => camp.Night && camp.Template == "DireWolf");
        Assert.Contains(camps, camp => camp.Night && camp.Template == "Wolf");
        Assert.Contains(camps, camp => camp.Template == "Wolf");
        Assert.Contains(camps, camp => camp.Template == "DireWolf" && !camp.Night);
        Assert.Equal(camps.Count, camps.Select(camp => camp.Key).Distinct().Count());

        // The day packs are the bulk of the world's wolves; the night adds to them rather than replacing them.
        Assert.Equal(21, camps.Where(camp => !camp.Night).Sum(camp => camp.Count));
        Assert.Equal(28, camps.Sum(camp => camp.Count));
    }

    [Fact]
    public void EveryShippedCampMember_StandsOnOpenFloor_OfTheDefaultWorld_OutsideTheWorldSpawnsReach()
    {
        WorldGenerator generator = new();
        IReadOnlyList<Dream.Simulation.World.Geometry.GeometryCellRecord> cells = generator.CreateStartupGeometry(
            WorldDefaults.StartupFloorSize, 8, WorldDefaults.StartupFloorSize, enrichTerrain: true);
        HashSet<(int X, int Z)> raised = [.. cells.Where(cell => cell.Key.Y >= 1).Select(cell => (cell.Key.X, cell.Key.Z))];
        float maxAggro = ContentCatalog.Shipped.Enemies.Max(template => template.AggroRange);

        foreach (SpawnCamp camp in ContentCatalog.Shipped.Camps.Where(camp => camp.ExistsOn(WorldDefaults.StartupFloorSize)))
        {
            for (int slot = 0; slot < camp.Count; slot++)
            {
                Vector3 spot = WolfSpawnPolicy.ResolveCampSpawn(camp.X, camp.Z, slot, camp.Count, WorldDefaults.StartupFloorSize, WorldDefaults.StartupFloorSize);
                int cellX = (int)MathF.Floor(spot.X);
                int cellZ = (int)MathF.Floor(spot.Z);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        Assert.DoesNotContain((cellX + dx, cellZ + dz), raised);
                    }
                }

                // Clear of the rim (the stone band within two cells of the edge) and of the world spawn.
                Assert.InRange(spot.X, 3f, WorldDefaults.StartupFloorSize - 4f);
                Assert.InRange(spot.Z, 3f, WorldDefaults.StartupFloorSize - 4f);
                Assert.True((spot.Xz - new Vector2(5f, 5f)).Length > maxAggro + 1f, $"{camp.Key}: {spot}");
            }
        }
    }

    [Fact]
    public void EveryFrontierCampMember_StandsOnOpenFloor_Of100mWorld_AtItsAbsoluteHome_AndTiersGrowWithDistance()
    {
        const int Size = ServerNewWorldSizeForTests;
        WorldGenerator generator = new();
        IReadOnlyList<Dream.Simulation.World.Geometry.GeometryCellRecord> cells = generator.CreateStartupGeometry(Size, 8, Size, enrichTerrain: true);
        HashSet<(int X, int Z)> raised = [.. cells.Where(cell => cell.Key.Y >= 1).Select(cell => (cell.Key.X, cell.Key.Z))];
        float maxAggro = ContentCatalog.Shipped.Enemies.Max(template => template.AggroRange);
        List<SpawnCamp> frontier = [.. ContentCatalog.Shipped.Camps.Where(camp => camp.IsAbsolute)];

        Assert.Equal(12, frontier.Count);
        Assert.All(frontier, camp => Assert.True(camp.ExistsOn(Size) && !camp.ExistsOn(Size - 1), camp.Key));
        Assert.All(ContentCatalog.Shipped.Camps.Where(camp => !camp.IsAbsolute), camp => Assert.False(camp.ExistsOn(Size), camp.Key));
        foreach (SpawnCamp camp in frontier)
        {
            for (int slot = 0; slot < camp.Count; slot++)
            {
                Vector3 spot = WolfSpawnPolicy.ResolveCampSpawn(camp.X, camp.Z, slot, camp.Count, Size, Size, absolute: true);
                Assert.True((spot.Xz - new Vector2(camp.X, camp.Z)).Length < 2f, $"{camp.Key}: {spot} is not at its metres");
                int cellX = (int)MathF.Floor(spot.X);
                int cellZ = (int)MathF.Floor(spot.Z);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        Assert.DoesNotContain((cellX + dx, cellZ + dz), raised);
                    }
                }

                Assert.InRange(spot.X, 3f, Size - 4f);
                Assert.InRange(spot.Z, 3f, Size - 4f);
                Assert.True((spot.Xz - new Vector2(5f, 5f)).Length > maxAggro + 1f, $"{camp.Key}: {spot}");
            }
        }

        // Tier order: by distance from the spawn (5, 5), the strongest level in a camp never drops, and the
        // nearest camps are the lowest tier.
        List<(float Distance, int Level)> tiers = [.. frontier
            .Select(camp => ((new Vector2(camp.X, camp.Z) - new Vector2(5f, 5f)).Length, ContentCatalog.Shipped.Enemies.Single(t => t.Key == camp.Template).Level))
            .OrderBy(tier => tier.Item1)];
        for (int i = 1; i < tiers.Count; i++)
        {
            Assert.True(tiers[i].Level >= tiers[i - 1].Level, $"{tiers[i - 1]} then {tiers[i]}");
        }

        Assert.All(tiers.Where(tier => tier.Distance < 30f), tier => Assert.Equal(1, tier.Level));
        Assert.Equal(8, tiers.Max(tier => tier.Level));
    }

    private const int ServerNewWorldSizeForTests = WorldDefaults.ServerNewWorldSize;

    [Fact]
    public void ACampSpawn_IsDeterministic_AndInsideTheMap()
    {
        Vector3 first = WolfSpawnPolicy.ResolveCampSpawn(30.5f, 30.5f, slot: 1, campCount: 2, width: 56, depth: 56);
        Assert.Equal(first, WolfSpawnPolicy.ResolveCampSpawn(30.5f, 30.5f, slot: 1, campCount: 2, width: 56, depth: 56));
        Assert.Equal(new Vector3(29f, 1f, 30.5f), first);
        Assert.Equal(new Vector3(30.5f, 1f, 30.5f), WolfSpawnPolicy.ResolveCampSpawn(30.5f, 30.5f, slot: 3, campCount: 1, width: 56, depth: 56));

        // A camp outside the map clamps in; a ring slot past the table wraps.
        Vector3 clamped = WolfSpawnPolicy.ResolveCampSpawn(500f, -4f, slot: 9, campCount: 4, width: 56, depth: 56);
        Assert.InRange(clamped.X, 0.5f, 54.5f);
        Assert.InRange(clamped.Z, 0.5f, 54.5f);
    }

    [Fact]
    public void ADigest_IsUnchangedWithoutCamps_AndChangesWithThem()
    {
        ContentCatalog none = ContentCatalog.Load(Read(ContentCatalog.ItemsFileName), Read(ContentCatalog.EnemiesFileName), Read(ContentCatalog.LootFileName));
        ContentCatalog explicitEmpty = Load(Camps());
        ContentCatalog one = Load(Camps(Camp()));
        ContentCatalog moved = Load(Camps(Camp(x: "11.5")));

        Assert.Empty(none.Camps);
        Assert.Equal(none.ContentDigest, explicitEmpty.ContentDigest);
        Assert.NotEqual(none.ContentDigest, one.ContentDigest);
        Assert.NotEqual(one.ContentDigest, moved.ContentDigest);
        Assert.Equal(one.ContentDigest, Load(Camps(Camp()).Replace(",", ", ")).ContentDigest);
    }

    [Theory]
    [InlineData("camp-a", "Goblin", "10", "10", "2", "5", "", "missing enemy")]
    [InlineData("", "Wolf", "10", "10", "2", "5", "", "")]
    [InlineData("camp-a", "Wolf", "-1", "10", "2", "5", "", "")]
    [InlineData("camp-a", "Wolf", "10", "10", "0", "5", "", "")]
    [InlineData("camp-a", "Wolf", "10", "10", "9", "5", "", "")]
    [InlineData("camp-a", "Wolf", "10", "10", "2", "0", "", "")]
    [InlineData("camp-a", "Wolf", "10", "10", "2", "5", ",\"night\":1", "true or false")]
    [InlineData("camp-a", "Wolf", "10", "10", "2", "5", ",\"extra\":1", "")]
    public void AnInvalidCamp_FailsTheLoad(string key, string template, string x, string z, string count, string respawn, string extra, string message)
    {
        ContentValidationException error = Assert.Throws<ContentValidationException>(
            () => Load(Camps(Camp(key, template, x, z, count, respawn, extra))));

        Assert.Contains(ContentCatalog.SpawnsFileName, error.Message, StringComparison.Ordinal);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateCampKey_FailsTheLoad()
    {
        ContentValidationException error = Assert.Throws<ContentValidationException>(
            () => Load(Camps(Camp("same"), Camp("same", x: "20.5"))));

        Assert.Contains("duplicate camp key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AContentDirectoryWithoutSpawnsJson_HasNoCamps()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dream-camps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string name in new[] { ContentCatalog.ItemsFileName, ContentCatalog.EnemiesFileName, ContentCatalog.LootFileName })
            {
                File.Copy(Path.Combine(ContentCatalog.ShippedDirectory, name), Path.Combine(directory, name));
            }

            Assert.Empty(ContentCatalog.LoadFromDirectory(directory).Camps);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- runtime ----

    private static long WorldTicksAt(int hour, WorldClockSettings? clock = null)
    {
        WorldClockSettings settings = clock ?? WorldClockSettings.Default;
        // The second day, so every hour is after the world began.
        return settings.TicksPerDay + (long)Math.Round(hour / 24d * settings.TicksPerDay) - settings.StartOffsetTicks;
    }

    private static WorldSessionRuntime CreateRuntime(ContentCatalog content, int hour, IWorldPersistenceRepository? world = null, SqliteSchemaBootstrapper? schema = null, WorldClockSettings? clock = null, bool fresh = true)
    {
        WorldSessionRuntime runtime = schema == null || world == null
            ? new(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub())
            : new(
                WorldDefaults.StartupFloorSize,
                new Vector3(5f, 1.5f, 5f),
                new SceneEventHub(),
                playerProfiles: new SqlitePlayerProfileRepository(schema),
                worldPersistence: world,
                persistenceWrites: PersistenceWriteQueue.Background());
        runtime.WriteCheckpointOnDispose = false;
        if (fresh)
        {
            // The test assembly's default content has no camps and spawns the first wolf of an empty world at
            // construction; a camp world starts empty.
            foreach (EnemyActor wolf in runtime.Enemies.ActiveActors.ToArray())
            {
                Kill(runtime, wolf);
            }
        }

        runtime.EnemyContent = content;
        runtime.ConfigureWorldClock(clock ?? WorldClockSettings.Default, WorldTicksAt(hour, clock));
        return runtime;
    }

    private static void Tick(WorldSessionRuntime runtime, ref ulong tick, int count = 1)
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

    private static void Kill(WorldSessionRuntime runtime, EnemyActor enemy)
    {
        int id = enemy.Combatant.Actor.Id;
        Assert.True(runtime.Enemies.Remove(id));
        typeof(WorldSessionRuntime)
            .GetMethod("DeletePersistedEnemy", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [id]);
    }

    private static int Active(WorldSessionRuntime runtime) => runtime.Enemies.ActiveActors.Count(enemy => enemy.IsActive);

    [Fact]
    public void ACampFillsOnTheFirstTicks_OneMemberPerCampPerTick_AtItsOwnSpot()
    {
        ContentCatalog content = Load(Camps(Camp("a", count: "3"), Camp("b", x: "30.5", z: "30.5", count: "1")));
        using WorldSessionRuntime runtime = CreateRuntime(content, hour: 12);
        Assert.Equal(0, Active(runtime));

        ulong tick = 0;
        Tick(runtime, ref tick);
        Assert.Equal(2, Active(runtime));
        Tick(runtime, ref tick, 2);
        Assert.Equal(4, Active(runtime));
        Tick(runtime, ref tick, 20);
        Assert.Equal(4, Active(runtime));
        Assert.All(
            runtime.Enemies.ActiveActors.Where(enemy => enemy.Position.X < 20f),
            enemy => Assert.True((enemy.Position.Xz - new Vector2(8.5f, 40.5f)).Length < 2f, enemy.Position.ToString()));
    }

    [Fact]
    public void ADeadMember_IsReplacedAfterRespawnSeconds_CountedInTicks_AndOnlyThen()
    {
        // One second is 64 ticks.
        ContentCatalog content = Load(Camps(Camp(count: "2", respawn: "1")));
        using WorldSessionRuntime runtime = CreateRuntime(content, hour: 12);
        ulong tick = 0;
        Tick(runtime, ref tick, 5);
        Assert.Equal(2, Active(runtime));

        Kill(runtime, runtime.Enemies.ActiveActors.First(enemy => enemy.IsActive));
        Assert.Equal(1, Active(runtime));

        // The timer starts on the tick that notices the death; nothing comes back before it runs out.
        Tick(runtime, ref tick, TicksPerSecond - 1);
        Assert.Equal(1, Active(runtime));
        Tick(runtime, ref tick, 1);
        int back = Active(runtime);
        if (back == 1)
        {
            Tick(runtime, ref tick);
            back = Active(runtime);
        }

        Assert.Equal(2, back);
        Tick(runtime, ref tick, 3 * TicksPerSecond);
        Assert.Equal(2, Active(runtime));
    }

    [Fact]
    public void TwoRuns_RefillAtTheSameTicks()
    {
        ContentCatalog content = Load(Camps(Camp(count: "2", respawn: "2"), Camp("b", x: "30.5", z: "30.5", count: "1", respawn: "3")));
        List<int> first = RefillTicks(content);
        List<int> second = RefillTicks(content);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    private static List<int> RefillTicks(ContentCatalog content)
    {
        using WorldSessionRuntime runtime = CreateRuntime(content, hour: 12);
        ulong tick = 0;
        Tick(runtime, ref tick, 4);
        List<int> changes = [];
        int active = Active(runtime);
        for (int i = 1; i <= 8 * TicksPerSecond; i++)
        {
            if (i == 10 || i == 200)
            {
                Kill(runtime, runtime.Enemies.ActiveActors.First(enemy => enemy.IsActive));
                active = Active(runtime);
            }

            Tick(runtime, ref tick);
            int now = Active(runtime);
            if (now != active)
            {
                changes.Add(i * 10 + now);
                active = now;
            }
        }

        return changes;
    }

    [Fact]
    public void ANightCamp_ExistsOnlyAtNight_AndDespawnsAtDawn()
    {
        ContentCatalog content = Load(Camps(Camp("day", count: "1"), Camp("dark", x: "30.5", z: "30.5", count: "2", extra: ",\"night\":true")));
        using WorldSessionRuntime day = CreateRuntime(content, hour: 12);
        ulong tick = 0;
        Tick(day, ref tick, 10);
        Assert.Equal(1, Active(day));

        // A one-minute day, started at 02:00: 320 ticks to 04:00.
        WorldClockSettings shortDay = WorldClockSettings.FromDayLengthMinutes(1);
        using WorldSessionRuntime night = CreateRuntime(content, hour: 2, clock: shortDay);
        tick = 0;
        Tick(night, ref tick, 10);
        Assert.Equal(WorldDayPhase.Night, night.WorldClock.Phase);
        Assert.Equal(3, Active(night));

        // Dawn: the night camp's members go one per tick, the day camp stays.
        Tick(night, ref tick, 330);
        Assert.NotEqual(WorldDayPhase.Night, night.WorldClock.Phase);
        Assert.Equal(1, Active(night));
        Assert.Equal(Active(night), night.Enemies.ActiveActors.Count(enemy => enemy.Position.X < 20f));
    }

    [Fact]
    public void AWorldWithoutCamps_KeepsTheWorldWidePopulation()
    {
        using WorldSessionRuntime runtime = LegacyWolfPopulation.AndTheFirstWolf(
            new WorldSessionRuntime(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub()));
        runtime.ConfigureWorldClock(WorldClockSettings.Default, WorldTicksAt(12));
        ulong tick = 0;

        Tick(runtime, ref tick, 30);

        Assert.Equal(1, Active(runtime));
    }

    // ---- persistence ----

    [Fact]
    public void ACampMember_IsPersistedWithItsCampKey_AndALegacyWolfWithNone()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);

        using (WorldSessionRuntime runtime = CreateRuntime(ContentCatalog.Shipped, hour: 12, world, database.Schema))
        {
            ulong tick = 0;
            Tick(runtime, ref tick, 10);
            Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
        }

        IReadOnlyList<PersistedEnemyInstance> rows = world.LoadActiveEnemies();
        Assert.Equal(ShippedDayPopulation, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.CampKey));
        Assert.Equal(2, rows.Count(row => row.CampKey == "north-east-wolves"));
        Assert.Equal(1, rows.Count(row => row.CampKey == "south-east-dire-wolf"));

        world.SaveEnemyInstance(Legacy(9001));
        Assert.Null(Assert.Single(world.LoadActiveEnemies(), row => row.ObjectId == new ObjectId(9001)).CampKey);
    }

    [Fact]
    public void TheCamps_SurviveARestart_WithTheirMembers_AndNoSecondSet()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        List<(string? Camp, int Count)> before;

        using (WorldSessionRuntime runtime = CreateRuntime(ContentCatalog.Shipped, hour: 12, world, database.Schema))
        {
            ulong tick = 0;
            Tick(runtime, ref tick, 10);
            Assert.Equal(ShippedDayPopulation, Active(runtime));
            runtime.PersistCheckpoint();
            Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
            before = Grouped(world.LoadActiveEnemies());
        }

        using (WorldSessionRuntime restarted = CreateRuntime(ContentCatalog.Shipped, hour: 12, world, database.Schema, fresh: false))
        {
            Assert.Equal(ShippedDayPopulation, Active(restarted));
            ulong tick = 0;
            Tick(restarted, ref tick, 4 * TicksPerSecond);

            Assert.Equal(ShippedDayPopulation, Active(restarted));
            Assert.Equal(before, Grouped(world.LoadActiveEnemies()));
            // Each restored member idles at its camp, not at a legacy spawn.
            foreach (EnemyActor enemy in restarted.Enemies.ActiveActors)
            {
                SpawnCamp camp = ContentCatalog.Shipped.Camps.OrderBy(c => (c.X - enemy.HomePosition.X) * (c.X - enemy.HomePosition.X) + (c.Z - enemy.HomePosition.Z) * (c.Z - enemy.HomePosition.Z)).First();
                Assert.True((camp.X - enemy.HomePosition.X) * (camp.X - enemy.HomePosition.X) + (camp.Z - enemy.HomePosition.Z) * (camp.Z - enemy.HomePosition.Z) < 2.5f * 2.5f, $"{camp.Key} {enemy.HomePosition}");
            }
        }
    }

    [Fact]
    public void ARestoredLegacyWolf_IsDroppedByTheFirstCampFill()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        world.SaveEnemyInstance(Legacy(9001));

        using WorldSessionRuntime runtime = CreateRuntime(ContentCatalog.Shipped, hour: 12, world, database.Schema, fresh: false);
        EnemyActor legacy = Assert.Single(runtime.Enemies.ActiveActors);
        Vector3 legacyHome = legacy.HomePosition;
        Assert.Equal(WolfSpawnPolicy.ResolveInitialSpawn(WorldDefaults.StartupFloorSize, WorldDefaults.StartupFloorSize), legacyHome);

        ulong tick = 0;
        Tick(runtime, ref tick, 10);

        // The first camp fill drops the legacy wolf (no camp_key) so the camps are the whole population.
        Assert.Equal(ShippedDayPopulation, Active(runtime));
        Assert.DoesNotContain(runtime.Enemies.ActiveActors, enemy => ReferenceEquals(enemy, legacy));
        Tick(runtime, ref tick, 4 * TicksPerSecond);
        Assert.Equal(ShippedDayPopulation, Active(runtime));
        Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(world.LoadActiveEnemies(), row => row.CampKey == null);
    }

    [Fact]
    public void ADeadCampMember_IsNotRestored_AndItsCampRefillsAfterTheRestart()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);

        using (WorldSessionRuntime runtime = CreateRuntime(ContentCatalog.Shipped, hour: 12, world, database.Schema))
        {
            ulong tick = 0;
            Tick(runtime, ref tick, 10);
            Kill(runtime, runtime.Enemies.ActiveActors.First(enemy => enemy.IsActive));
            Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
        }

        Assert.Equal(ShippedDayPopulation - 1, world.LoadActiveEnemies().Count);
        using (WorldSessionRuntime restarted = CreateRuntime(ContentCatalog.Shipped, hour: 12, world, database.Schema, fresh: false))
        {
            Assert.Equal(ShippedDayPopulation - 1, Active(restarted));
            ulong tick = 0;
            Tick(restarted, ref tick, 5);
            Assert.Equal(ShippedDayPopulation, Active(restarted));
        }
    }

    // ---- placement on any map size (fix loop) ----

    private static bool InSolid(Map map, Vector3 feet)
    {
        Vector3 resolved = WorldSessionRuntime.ResolveRespawnPosition(map, new Vector3(feet.X, feet.Y + 0.5f, feet.Z));
        return resolved.Y - 0.5f - feet.Y > 0.001f;
    }

    private static void AssertPlacedSanely(WorldSessionRuntime runtime, string label)
    {
        List<EnemyActor> members = [.. runtime.Enemies.ActiveActors.Where(enemy => enemy.IsActive)];
        foreach (EnemyActor member in members)
        {
            Vector3 home = member.HomePosition;
            Assert.InRange(home.X, 0.5f, runtime.Map.Width - 0.5f);
            Assert.InRange(home.Z, 0.5f, runtime.Map.Depth - 0.5f);
            Assert.False(InSolid(runtime.Map, home), $"{label}: {member.EnemyKind} at {home} is in solid terrain");
            Assert.True((home.Xz - new Vector2(5f, 5f)).Length > member.AggroRange, $"{label}: {member.EnemyKind} at {home} is inside the world spawn's aggro range");
        }

        for (int i = 0; i < members.Count; i++)
        {
            for (int j = i + 1; j < members.Count; j++)
            {
                Assert.True((members[i].HomePosition.Xz - members[j].HomePosition.Xz).Length >= 1f, $"{label}: two members stand at {members[i].HomePosition}");
            }
        }
    }

    private static WorldSessionRuntime CreateSizedRuntime(int size, ContentCatalog content, int hour)
    {
        WorldSessionRuntime runtime = new(size, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.WriteCheckpointOnDispose = false;
        foreach (EnemyActor wolf in runtime.Enemies.ActiveActors.ToArray())
        {
            Kill(runtime, wolf);
        }

        runtime.EnemyContent = content;
        runtime.ConfigureWorldClock(WorldClockSettings.Default, WorldTicksAt(hour));
        return runtime;
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(32)]
    [InlineData(42)]
    [InlineData(56)]
    [InlineData(88)]
    [InlineData(100)]
    public void TheShippedCamps_OnAnyMapSize_StandInOpenCellsOutsideTheSpawnsReach_WithoutStacking(int size)
    {
        foreach (int hour in new[] { 12, 23 })
        {
            using WorldSessionRuntime runtime = CreateSizedRuntime(size, ContentCatalog.Shipped, hour);
            ulong tick = 0;
            Tick(runtime, ref tick, 12);

            AssertPlacedSanely(runtime, $"size {size} hour {hour}");
            if (size >= 100)
            {
                // The frontier camps (M9): 24 members by day; the night adds the hunt (3) and the two risen skeleton camps (6) in the dark.
                Assert.Equal(hour == 12 ? 24 : 33, Active(runtime));
            }
            else if (size >= 56)
            {
                Assert.Equal(hour == 12 ? 21 : 28, Active(runtime));
            }
        }
    }

    [Fact]
    public void ACampOnAHandBuiltMap_AvoidsItsSolidCubes()
    {
        using WorldSessionRuntime runtime = CreateSizedRuntime(56, Load(Camps(Camp("walled", x: "30.5", z: "30.5", count: "4"))), 12);
        // A 5x5 block of cubes, two high, over the camp's centre and ring.
        for (int x = 28; x <= 32; x++)
        {
            for (int z = 28; z <= 32; z++)
            {
                for (int y = 1; y <= 2; y++)
                {
                    runtime.Map.TryAddCube(new Vector3(x, y, z), isStatic: true, withPhysics: false);
                }
            }
        }

        ulong tick = 0;
        Tick(runtime, ref tick, 10);

        Assert.Equal(4, Active(runtime));
        AssertPlacedSanely(runtime, "hand-built");
    }

    [Fact]
    public void AMemberWithNoFreePlace_IsSkipped_NotStacked()
    {
        // A 5 m world: all of it is inside the spawn's aggro range, so no member qualifies.
        using WorldSessionRuntime runtime = CreateSizedRuntime(5, Load(Camps(Camp(count: "3"))), 12);
        ulong tick = 0;
        Tick(runtime, ref tick, 20);

        Assert.Equal(0, Active(runtime));
    }

    [Fact]
    public void ACampKeepsItsPlaceOnTheMap_WhenTheMapIsLargerThanTheDesignSize()
    {
        using WorldSessionRuntime runtime = CreateSizedRuntime(112, Load(Camps(Camp(x: "28", z: "28", count: "1"))), 12);
        ulong tick = 0;
        Tick(runtime, ref tick, 3);

        EnemyActor member = Assert.Single(runtime.Enemies.ActiveActors);
        Assert.True((member.HomePosition.Xz - new Vector2(56f, 56f)).Length < 8f, member.HomePosition.ToString());
    }

    [Fact]
    public void ADawnDespawn_WaitsForAnEnemyWithContributors()
    {
        ContentCatalog content = Load(Camps(Camp("dark", x: "30.5", z: "30.5", count: "2", extra: ",\"night\":true")));
        WorldClockSettings shortDay = WorldClockSettings.FromDayLengthMinutes(1);
        using WorldSessionRuntime runtime = CreateRuntime(content, hour: 2, clock: shortDay);
        ulong tick = 0;
        Tick(runtime, ref tick, 10);
        Assert.Equal(2, Active(runtime));
        EnemyActor fought = runtime.Enemies.ActiveActors.OrderBy(enemy => enemy.Combatant.Actor.Id).Last();
        PlayerSessionId fighter = new("fighter");
        fought.RecordDamage(fighter, 5);

        Tick(runtime, ref tick, 400);
        Assert.NotEqual(WorldDayPhase.Night, runtime.WorldClock.Phase);
        Assert.Equal(1, Active(runtime));
        Assert.Contains(runtime.Enemies.ActiveActors, enemy => ReferenceEquals(enemy, fought) && enemy.IsActive);

        fought.ClearContributor(fighter);
        Tick(runtime, ref tick, 5);
        Assert.Equal(0, Active(runtime));
    }

    [Fact]
    public void ClearContributor_DropsOnlyThatSessionsCredit()
    {
        PlayerSessionId a = new("a");
        PlayerSessionId b = new("b");
        using WorldSessionRuntime runtime = CreateRuntime(Load(Camps(Camp(count: "1"))), hour: 12);
        ulong tick = 0;
        Tick(runtime, ref tick, 3);
        EnemyActor enemy = Assert.Single(runtime.Enemies.ActiveActors);
        enemy.RecordDamage(a, 3);
        enemy.RecordDamage(b, 4);

        enemy.ClearContributor(a);

        EnemyDamageContribution left = Assert.Single(enemy.Contributors.ToArray());
        Assert.Equal(b, left.SessionId);
        Assert.Equal(4, left.Damage);

        // A later session reusing the id starts from nothing.
        enemy.RecordDamage(a, 1);
        Assert.Equal(1, enemy.Contributors.ToArray().Single(entry => entry.SessionId == a).Damage);
    }

    [Fact]
    public void ARestartedCamp_PlacesItsNewMembers_AwayFromTheRestoredOnes()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        using (WorldSessionRuntime runtime = CreateRuntime(ContentCatalog.Shipped, hour: 12, world, database.Schema))
        {
            ulong tick = 0;
            Tick(runtime, ref tick, 10);
            Kill(runtime, runtime.Enemies.ActiveActors.First(enemy => enemy.IsActive && enemy.Position.X > 40f && enemy.Position.Z < 20f));
            Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
        }

        using WorldSessionRuntime restarted = CreateRuntime(ContentCatalog.Shipped, hour: 12, world, database.Schema, fresh: false);
        ulong next = 0;
        // The camp refills at once after a restart (its timers are not saved); the new member must not stack.
        Tick(restarted, ref next, 5);

        Assert.Equal(ShippedDayPopulation, Active(restarted));
        AssertPlacedSanely(restarted, "restart");
    }

    private static List<(string? Camp, int Count)> Grouped(IReadOnlyList<PersistedEnemyInstance> rows) =>
        [.. rows.GroupBy(row => row.CampKey).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => (group.Key, group.Count()))];

    private static PersistedEnemyInstance Legacy(int id)
    {
        DateTime now = DateTime.UtcNow;
        return new PersistedEnemyInstance(
            new ObjectId(id),
            "Wolf",
            "Wolf",
            new Vector3(40.5f, 1f, 40.5f),
            Vector3.UnitZ,
            new PersistedCreatureStats(100, 100, 10, 10, 10, 0, 2.8f, 0.952381f, 1),
            now,
            now);
    }
}
