using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Tests.Server.Persistence;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Content;

// M9: spawn camps and quests may carry a world-size band (minWorldSize / maxWorldSize on a camp, minWorldSize on a
// quest). A camp with a minimum is authored in metres and never scaled; content without the fields is unchanged.
public sealed class CampBandTests
{
    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, fileName));

    private static ContentCatalog LoadCamps(string spawnsJson) =>
        ContentCatalog.Load(Read(ContentCatalog.ItemsFileName), Read(ContentCatalog.EnemiesFileName), Read(ContentCatalog.LootFileName), spawnsJson);

    private static ContentCatalog LoadQuests(string questsJson) =>
        ContentCatalog.Load(
            Read(ContentCatalog.ItemsFileName),
            Read(ContentCatalog.EnemiesFileName),
            Read(ContentCatalog.LootFileName),
            Read(ContentCatalog.SpawnsFileName),
            Read(ContentCatalog.NpcsFileName),
            Read(ContentCatalog.SkillsFileName),
            questsJson);

    private static string Camps(params string[] camps) => "{\"version\":1,\"camps\":[" + string.Join(",", camps) + "]}";

    private static string Camp(string key, string x, string z, string extra = "", string count = "1") =>
        $"{{\"key\":\"{key}\",\"template\":\"Wolf\",\"x\":{x},\"z\":{z},\"count\":{count},\"respawnSeconds\":30{extra}}}";

    private static string Quest(string extra = "") =>
        "{\"version\":1,\"quests\":[{\"key\":\"q\",\"title\":\"T\",\"description\":\"D\",\"giver\":\"gate-warden\"," +
        "\"objectives\":[{\"kind\":\"Kill\",\"target\":\"Wolf\",\"count\":1}],\"rewards\":{\"xp\":1,\"gold\":1,\"items\":[]}" + extra + "}]}";

    [Fact]
    public void ACampWithoutABand_ExistsOnEverySize_AndIsNotAbsolute()
    {
        SpawnCamp camp = Assert.Single(LoadCamps(Camps(Camp("a", "8.5", "40.5"))).Camps);

        Assert.False(camp.IsAbsolute);
        Assert.True(camp.ExistsOn(WorldDescriptor.MinHorizontalSize));
        Assert.True(camp.ExistsOn(WorldDescriptor.MaxHorizontalSize));
    }

    [Fact]
    public void TheBand_IsInclusive_OnBothEnds()
    {
        SpawnCamp camp = Assert.Single(LoadCamps(Camps(Camp("a", "8.5", "40.5", ",\"minWorldSize\":60,\"maxWorldSize\":80"))).Camps);

        Assert.True(camp.IsAbsolute);
        Assert.False(camp.ExistsOn(59));
        Assert.True(camp.ExistsOn(60));
        Assert.True(camp.ExistsOn(80));
        Assert.False(camp.ExistsOn(81));
    }

    [Fact]
    public void AMaximumAlone_BoundsTheCampFromAbove_AndLeavesItScaled()
    {
        SpawnCamp camp = Assert.Single(LoadCamps(Camps(Camp("a", "8.5", "40.5", ",\"maxWorldSize\":99"))).Camps);

        Assert.False(camp.IsAbsolute);
        Assert.True(camp.ExistsOn(56));
        Assert.True(camp.ExistsOn(99));
        Assert.False(camp.ExistsOn(100));
    }

    [Theory]
    [InlineData(",\"minWorldSize\":80,\"maxWorldSize\":60")]
    [InlineData(",\"minWorldSize\":4")]
    [InlineData(",\"minWorldSize\":101")]
    [InlineData(",\"maxWorldSize\":101")]
    [InlineData(",\"minWorldSize\":\"100\"")]
    [InlineData(",\"minWorldSize\":60.5")]
    public void ABadBand_IsRejectedAtLoad(string extra)
    {
        Assert.Throws<ContentValidationException>(() => LoadCamps(Camps(Camp("a", "8.5", "40.5", extra))));
    }

    [Theory]
    [InlineData("60.5", "20.5")]
    [InlineData("20.5", "60.5")]
    [InlineData("60", "20")]
    public void AnAbsoluteCamp_OutsideItsMinimumSize_IsRejectedAtLoad(string x, string z)
    {
        Assert.Throws<ContentValidationException>(() => LoadCamps(Camps(Camp("a", x, z, ",\"minWorldSize\":60"))));
    }

    [Fact]
    public void TheBandFields_ChangeTheDigest_ButAbsentFieldsKeepIt()
    {
        string plain = LoadCamps(Camps(Camp("a", "8.5", "40.5"))).ContentDigest;
        string banded = LoadCamps(Camps(Camp("a", "8.5", "40.5", ",\"maxWorldSize\":99"))).ContentDigest;
        string absolute = LoadCamps(Camps(Camp("a", "8.5", "40.5", ",\"minWorldSize\":60"))).ContentDigest;

        Assert.NotEqual(plain, banded);
        Assert.NotEqual(plain, absolute);
        Assert.NotEqual(banded, absolute);
        Assert.Equal(plain, LoadCamps(Camps(Camp("a", "8.5", "40.5"))).ContentDigest);
    }

    [Fact]
    public void ACampPlacement_ScalesWhenLegacy_AndIsInMetresWhenAbsolute()
    {
        Vector3 scaled = WolfSpawnPolicy.ResolveCampSpawn(28f, 14f, slot: 0, campCount: 1, width: 100, depth: 100);
        Vector3 absolute = WolfSpawnPolicy.ResolveCampSpawn(28f, 14f, slot: 0, campCount: 1, width: 100, depth: 100, absolute: true);

        Assert.Equal(new Vector3(28f * 100f / 56f, 1f, 14f * 100f / 56f), scaled);
        Assert.Equal(new Vector3(28f, 1f, 14f), absolute);
        Assert.Equal(
            WolfSpawnPolicy.ResolveCampSpawn(28f, 14f, 0, 1, 56, 56),
            WolfSpawnPolicy.ResolveCampSpawn(28f, 14f, 0, 1, 56, 56, absolute: false));
    }

    [Fact]
    public void AQuestWithAMinimum_IsOfferedOnlyOnBigEnoughWorlds_AndTheFieldIsOptional()
    {
        QuestDefinition plain = Assert.Single(LoadQuests(Quest()).Quests);
        QuestDefinition tier = Assert.Single(LoadQuests(Quest(",\"minWorldSize\":100")).Quests);

        Assert.True(plain.ExistsOn(WorldDescriptor.MinHorizontalSize));
        Assert.False(tier.ExistsOn(99));
        Assert.True(tier.ExistsOn(100));
        Assert.NotEqual(LoadQuests(Quest()).ContentDigest, LoadQuests(Quest(",\"minWorldSize\":100")).ContentDigest);
        Assert.Throws<ContentValidationException>(() => LoadQuests(Quest(",\"minWorldSize\":101")));
        Assert.Throws<ContentValidationException>(() => LoadQuests(Quest(",\"minWorldSize\":\"x\"")));
    }

    // ---- runtime ----

    private static WorldSessionRuntime CreateRuntime(int size, ContentCatalog content)
    {
        WorldSessionRuntime runtime = new(size, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.WriteCheckpointOnDispose = false;
        foreach (EnemyActor wolf in runtime.Enemies.ActiveActors.ToArray())
        {
            Assert.True(runtime.Enemies.Remove(wolf.Combatant.Actor.Id));
        }

        runtime.EnemyContent = content;
        WorldClockSettings clock = WorldClockSettings.Default;
        runtime.ConfigureWorldClock(clock, clock.TicksPerDay + (long)Math.Round(12 / 24d * clock.TicksPerDay) - clock.StartOffsetTicks);
        return runtime;
    }

    private static void Tick(WorldSessionRuntime runtime, int count)
    {
        for (ulong tick = 1; tick <= (ulong)count; tick++)
        {
            runtime.AdvanceAuthoritativeTick(
                tick,
                SimulationClock.FixedDeltaSeconds,
                default(PlayerCommandFrame),
                PlayerSimulationTuning.AuthoritativeDefault);
        }
    }

    [Fact]
    public void ABandedCamp_FillsOnlyInsideItsBand_AndAnAbsoluteOneStandsAtItsMetres()
    {
        ContentCatalog content = LoadCamps(Camps(
            Camp("legacy", "40.5", "40.5", ",\"maxWorldSize\":99", count: "2"),
            Camp("frontier", "70.5", "20.5", ",\"minWorldSize\":100", count: "1")));

        using (WorldSessionRuntime small = CreateRuntime(56, content))
        {
            Tick(small, 12);
            Assert.Equal(2, small.Enemies.ActiveActors.Count(enemy => enemy.IsActive));
            Assert.All(small.Enemies.ActiveActors, enemy => Assert.True((enemy.Position.Xz - new Vector2(40.5f, 40.5f)).Length < 3f));
        }

        using (WorldSessionRuntime big = CreateRuntime(100, content))
        {
            Tick(big, 12);
            EnemyActor only = Assert.Single(big.Enemies.ActiveActors, enemy => enemy.IsActive);
            Assert.True((only.Position.Xz - new Vector2(70.5f, 20.5f)).Length < 1f, only.Position.ToString());
        }
    }

    [Fact]
    public void ANightCamp_InsideItsBand_StillWaitsForNight()
    {
        ContentCatalog content = LoadCamps(Camps(Camp("dark", "70.5", "20.5", ",\"minWorldSize\":100,\"night\":true")));

        using WorldSessionRuntime day = CreateRuntime(100, content);
        Tick(day, 12);
        Assert.Empty(day.Enemies.ActiveActors.Where(enemy => enemy.IsActive));
    }

    [Fact]
    public void RestoredLegacyCampRows_OnA100MeterWorld_AreRemovedOnePerTickWhenIdle_FightersAreSpared_AndTheirRowsGoAway()
    {
        // The legacy camp stops at 99 m, so on a 100 m world its restored members have no camp to belong to.
        ContentCatalog content = LoadCamps(Camps(Camp("north-east-wolves", "40.5", "40.5", ",\"maxWorldSize\":99", count: "3")));
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < 3; i++)
        {
            world.SaveEnemyInstance(new PersistedEnemyInstance(
                new ObjectId(9001 + i), "Wolf", "Wolf", new Vector3(40.5f + i, 1f, 40.5f), Vector3.UnitZ,
                new PersistedCreatureStats(100, 100, 10, 10, 10, 0, 2.8f, 0.952381f, 1), now, now, CampKey: "north-east-wolves"));
        }

        Assert.Equal(3, world.LoadActiveEnemies().Count(row => row.CampKey == "north-east-wolves"));

        using WorldSessionRuntime runtime = new(
            100,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: world,
            persistenceWrites: PersistenceWriteQueue.Background());
        runtime.WriteCheckpointOnDispose = false;
        runtime.EnemyContent = content;
        WorldClockSettings clock = WorldClockSettings.Default;
        runtime.ConfigureWorldClock(clock, clock.TicksPerDay + (long)Math.Round(12 / 24d * clock.TicksPerDay) - clock.StartOffsetTicks);

        Assert.Equal(3, runtime.Enemies.ActiveActors.Count(enemy => enemy.IsActive));
        // A restored member walks home from its saved spot (and counts as chasing on the way): start them at home, idle.
        foreach (EnemyActor member in runtime.Enemies.ActiveActors)
        {
            member.SetAuthoritativeState(member.HomePosition, member.Forward, EnemyBehaviorState.Idle);
        }

        EnemyActor fighter = runtime.Enemies.ActiveActors.OrderBy(enemy => enemy.Combatant.Actor.Id).First();
        PlayerSessionId attacker = new("attacker");
        fighter.RecordDamage(attacker, 5);

        Tick(runtime, 1);
        Assert.Equal(2, runtime.Enemies.ActiveActors.Count(enemy => enemy.IsActive));
        Tick(runtime, 1);
        Assert.Equal(1, runtime.Enemies.ActiveActors.Count(enemy => enemy.IsActive));
        Tick(runtime, 10);
        EnemyActor spared = Assert.Single(runtime.Enemies.ActiveActors, enemy => enemy.IsActive);
        Assert.Same(fighter, spared);
        Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
        Assert.Single(world.LoadActiveEnemies());

        fighter.ClearContributor(attacker);
        Tick(runtime, 2);
        Assert.Empty(runtime.Enemies.ActiveActors.Where(enemy => enemy.IsActive));
        Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
        Assert.Empty(world.LoadActiveEnemies());
    }
}
