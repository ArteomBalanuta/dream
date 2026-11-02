using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Tests.Support;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Server.Persistence;

// Wave G (G2): Agility, Energy and MovementSpeed are saved as stored. PersistPlayerProfile used to write the
// constants 10, 10 and 6 on every leave, so a hand-edited row was reset the first time its player left.
public sealed class RealProfileStatsTests
{
    private const string Player = "Stat Player";
    private const int StoredAgility = 7;
    private const int StoredEnergy = 3;
    private const float StoredMovementSpeed = 4.5f;

    [Fact]
    public void StoredAgilityEnergyAndMovementSpeed_SurviveJoin_Leave_Checkpoint_AndRestart()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        PersistedPlayerProfile created = profiles.GetOrCreate(Player);
        profiles.Save(created with
        {
            Stats = created.Stats with { Agility = StoredAgility, Energy = StoredEnergy, MovementSpeed = StoredMovementSpeed }
        });

        using (DedicatedServerHost first = CreateHost(database))
        {
            Join(first, "conn-1", "stat-1");
            // A full checkpoint while the player is attached, then the leave's profile save.
            Assert.NotNull(first.Runtime!.CapturePersistenceCheckpoint(full: true));
            Leave(first, "conn-1");
            AssertStoredStats(profiles);
        }

        // A restart reads the row back and saves it again unchanged.
        using (DedicatedServerHost second = CreateHost(database))
        {
            Join(second, "conn-2", "stat-2");
            Leave(second, "conn-2");
        }

        AssertStoredStats(profiles);
    }

    [Fact]
    public void ADefaultRow_StaysTenTenAndSix_AfterAJoinAndLeave()
    {
        using PersistenceTestDatabase database = new();
        using DedicatedServerHost host = CreateHost(database);

        Join(host, "conn-1", "stat-1");
        Leave(host, "conn-1");

        PersistedCreatureStats stats = new SqlitePlayerProfileRepository(database.Schema).TryGet(Player)!.Value.Stats;
        Assert.Equal((10, 10, 6f), (stats.Agility, stats.Energy, stats.MovementSpeed));
    }

    [Fact]
    public void ChangedLevelAndBaseStats_AreSaved_WhileTheUnsimulatedStatsKeepTheirStoredValues()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        PersistedPlayerProfile created = profiles.GetOrCreate(Player);
        profiles.Save(created with
        {
            Stats = created.Stats with { Agility = StoredAgility, Strength = 25, Level = 4, Experience = 120 }
        });
        using DedicatedServerHost host = CreateHost(database);

        Join(host, "conn-1", "stat-1");
        Leave(host, "conn-1");

        PersistedCreatureStats stats = profiles.TryGet(Player)!.Value.Stats;
        Assert.Equal(StoredAgility, stats.Agility);
        Assert.Equal(25, stats.Strength);
        Assert.Equal(4, stats.Level);
        Assert.Equal(120, stats.Experience);
    }

    [Fact]
    public void SpentAttributePoints_AreSaved_AndSurviveARestart()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);

        using (DedicatedServerHost first = CreateHost(database))
        {
            Join(first, "conn-1", "stat-1");
            Dream.Simulation.Players.PlayerCombatant combatant = first.Runtime!.Player.Combatant;
            combatant.GrantStatPoints(5);
            Assert.True(combatant.TrySpendPoint(Dream.Simulation.Stats.StatId.Energy, 2, out _));
            Leave(first, "conn-1");
        }

        PersistedCreatureStats stored = profiles.TryGet(Player)!.Value.Stats;
        Assert.Equal((12, 3), (stored.Energy, stored.StatPoints));

        using DedicatedServerHost second = CreateHost(database);
        Join(second, "conn-2", "stat-2");
        Dream.Simulation.Players.PlayerCombatant restored = second.Runtime!.Player.Combatant;
        Assert.Equal(12, restored.BaseStats.Energy);
        Assert.Equal(3, restored.StatPoints);
        Assert.Equal(80, restored.MaxMana);
    }

    private static void AssertStoredStats(SqlitePlayerProfileRepository profiles)
    {
        PersistedCreatureStats stats = profiles.TryGet(Player)!.Value.Stats;
        Assert.Equal((StoredAgility, StoredEnergy, StoredMovementSpeed), (stats.Agility, stats.Energy, stats.MovementSpeed));
    }

    // Accountless host: no repository, so a join needs no token and reads the row by the typed name.
    private static DedicatedServerHost CreateHost(PersistenceTestDatabase database) =>
        AccountHostHarness.CreateProductionHost(database, accounts: null);

    private static void Join(DedicatedServerHost host, string connectionId, string sessionId)
    {
        Assert.Empty(host.HandleClientMessage(connectionId, new ClientJoinRequestMessage(sessionId, Player)));
        Flush(host);
        Tick(host);
        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(replies).Message);
        host.DrainPendingActionResults();
        Flush(host);
    }

    private static void Leave(DedicatedServerHost host, string connectionId)
    {
        host.ReleaseConnection(connectionId);
        Tick(host);
        Flush(host);
    }

    private static void Flush(DedicatedServerHost host) =>
        Assert.True(host.Runtime!.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);

    private static void Tick(DedicatedServerHost host)
    {
        int executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        if (executed == 0)
        {
            executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
        }

        Assert.Equal(1, executed);
    }
}
