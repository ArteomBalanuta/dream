using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Support;

namespace Dream.Tests.Server;

// Profiles are global and two worlds can be hosted at once, so a world's cached copy of a profile can be
// older than the row another world wrote. A restore takes the prefetched row when it is newer (SaveProfile
// stamps the copy it caches); these run two live hosts over one real SQLite database.
public sealed class ProgressBetweenWorldsTests
{
    private const string Hero = "Drifter";

    private static string Token => TestAccountTokens.For("drifter");

    private static void Play(AccountHostHarness world, string session, int level, int experience, int strength, int vitality)
    {
        PersistedPlayerProfile profile = new SqlitePlayerProfileRepository(world.Database.Schema).TryGet(Hero)!.Value;
        world.Runtime.ApplyPersistedPlayerState(
            new PlayerSessionId(session),
            profile with { Stats = profile.Stats with { Level = level, Experience = experience, Strength = strength, Vitality = vitality } },
            null);
    }

    private static void AssertProgress(AccountHostHarness world, int level, int experience, int strength, int vitality)
    {
        var combatant = world.Runtime.Player.Combatant;
        Assert.Equal(level, combatant.Level);
        Assert.Equal(experience, combatant.Experience);
        Assert.Equal(strength, combatant.BaseStats.Strength);
        Assert.Equal(vitality, combatant.BaseStats.Vitality);
    }

    private static void AssertRow(PersistenceTestDatabase database, int level, int experience, int strength, int vitality)
    {
        PersistedCreatureStats stats = new SqlitePlayerProfileRepository(database.Schema).TryGet(Hero)!.Value.Stats;
        Assert.Equal((level, experience, strength, vitality), (stats.Level, stats.Experience, stats.Strength, stats.Vitality));
    }

    [Fact]
    public void TwoLiveWorldHosts_ProgressMadeInEitherWorld_SurvivesTheTripBackAndForth()
    {
        using PersistenceTestDatabase database = new();
        new SqliteWorldCatalog(database.Schema).Create(new WorldCreateOptions("second world"));
        using AccountHostHarness one = AccountHostHarness.OverSqlite(database);
        using AccountHostHarness two = AccountHostHarness.OverSqlite(database, "second world");

        // Level 7 in world one, then away: world one caches the departed profile at level 7.
        one.JoinAccepted("c1", "s1", Hero, Token);
        Play(one, "s1", 7, 321, 25, 18);
        one.Leave("c1");

        // World two sees it and goes further.
        two.JoinAccepted("c1", "s1", Hero, Token);
        AssertProgress(two, 7, 321, 25, 18);
        Play(two, "s1", 8, 40, 26, 19);
        two.Leave("c1");
        AssertRow(database, 8, 40, 26, 19);

        // Back in world one, whose cache still holds level 7: the newer row wins and stays.
        one.JoinAccepted("c2", "s1", Hero, Token);
        AssertProgress(one, 8, 40, 26, 19);
        one.Leave("c2");
        AssertRow(database, 8, 40, 26, 19);

        // The other direction: world one's progress is not lost to world two's older cache.
        one.JoinAccepted("c3", "s1", Hero, Token);
        Play(one, "s1", 9, 5, 27, 20);
        one.Leave("c3");
        two.JoinAccepted("c2", "s1", Hero, Token);
        AssertProgress(two, 9, 5, 27, 20);
        two.Leave("c2");
        AssertRow(database, 9, 5, 27, 20);
    }

    [Fact]
    public void LeavingAndRejoiningTheSameWorldAtOnce_StillGetsTheNewestData()
    {
        using PersistenceTestDatabase database = new();
        using AccountHostHarness world = AccountHostHarness.OverSqlite(database);
        world.JoinAccepted("c1", "s1", Hero, Token);
        Play(world, "s1", 4, 10, 14, 11);

        // The leave queues the save, the rejoin's prefetch queues behind it; neither is waited for.
        world.Host.ReleaseConnection("c1");
        world.SendJoin("c2", "s2", Hero, Token);
        world.Tick();
        world.Tick();
        Assert.IsType<JoinAcceptedMessage>(world.RepliesOf("c2")[^1]);
        AssertProgress(world, 4, 10, 14, 11);

        Play(world, "s2", 5, 11, 15, 12);
        world.Leave("c2");
        world.JoinAccepted("c3", "s3", Hero, Token);
        AssertProgress(world, 5, 11, 15, 12);
        AssertRow(database, 5, 11, 15, 12);
    }
}
