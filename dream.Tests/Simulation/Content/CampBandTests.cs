using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
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
}
