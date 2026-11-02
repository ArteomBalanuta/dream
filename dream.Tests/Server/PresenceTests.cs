using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server.Persistence.Repositories;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Support;

namespace Dream.Tests.Server;

// M8 (B9). The character presence lease: a character is played in one world at a time. Two real hosts on
// one real SQLite database, one per world, and the repository SQL on its own.
public sealed class PresenceTests
{
    private const string Alice = "Alice Presence";

    private static readonly DateTime Now = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static AccountKey KeyOf(string name) => TestAccountTokens.KeyFor(name);

    private static long Leases(PersistenceTestDatabase db) => db.Count("SELECT COUNT(*) FROM character_presence;");

    private static void FlushWrites(AccountHostHarness harness) =>
        Assert.True(harness.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));

    [Fact]
    public void ASecondWorld_IsRefused_WhileTheFirstHoldsTheCharacter_ThenFreeAfterLeave()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness first = AccountHostHarness.OverSqlite(db);
        using AccountHostHarness second = AccountHostHarness.OverSqlite(db, world: "Second");
        first.JoinAccepted("c1", "s1", Alice);
        Assert.Equal(1, Leases(db));

        string refusal = second.JoinRejectionOf("d1", "t1", Alice, TestAccountTokens.For(Alice));

        Assert.Equal(JoinRejectionReasons.OnlineElsewhere, refusal);
        Assert.False(second.Host.IsConnectionJoined("d1"));

        first.Leave("c1");
        Assert.Equal(0, Leases(db));
        second.JoinAccepted("d2", "t2", Alice);
        Assert.Equal(1, Leases(db));
        Assert.Equal(second.Runtime.World.Id.Value, db.Count("SELECT world_id FROM character_presence;"));
    }

    [Fact]
    public void AnExpiredLease_IsTakenOver_ByAnotherWorld()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness first = AccountHostHarness.OverSqlite(db);
        using AccountHostHarness second = AccountHostHarness.OverSqlite(db, world: "Second");
        first.JoinAccepted("c1", "s1", Alice);
        Assert.Equal(JoinRejectionReasons.OnlineElsewhere, second.JoinRejectionOf("d1", "t1", Alice, TestAccountTokens.For(Alice)));

        // The first server crashed: nothing renews the lease, and it runs out.
        using (Microsoft.Data.Sqlite.SqliteConnection connection = db.Schema.CreateOpenConnection())
        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE character_presence SET expires_utc = '2000-01-01T00:00:00.0000000Z';";
            command.ExecuteNonQuery();
        }

        second.JoinAccepted("d2", "t2", Alice);
        Assert.Equal(second.Runtime.World.Id.Value, db.Count("SELECT world_id FROM character_presence;"));
    }

    [Fact]
    public void TheSameWorld_ReclaimsAtOnce()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        harness.JoinAccepted("c1", "s1", Alice);

        // The connection drops without a clean release being seen yet; the same world joins again.
        harness.Host.ReleaseConnection("c1");
        harness.JoinAccepted("c2", "s2", Alice);

        Assert.Equal(1, Leases(db));
    }

    [Fact]
    public void ALeaseIsRenewed_EveryNineHundredSixtyTicks_WhileThePlayerIsHere()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        harness.JoinAccepted("c1", "s1", Alice);
        void Shorten()
        {
            using Microsoft.Data.Sqlite.SqliteConnection connection = db.Schema.CreateOpenConnection();
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"UPDATE character_presence SET expires_utc = '{Dream.Server.Persistence.SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow.AddSeconds(3))}';";
            command.ExecuteNonQuery();
        }

        Shorten();
        ulong start = harness.Runtime.CurrentServerTick;
        while (harness.Runtime.CurrentServerTick < start + 1000)
        {
            harness.Host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        }

        FlushWrites(harness);
        using Microsoft.Data.Sqlite.SqliteConnection read = db.Schema.CreateOpenConnection();
        using Microsoft.Data.Sqlite.SqliteCommand select = read.CreateCommand();
        select.CommandText = "SELECT expires_utc FROM character_presence;";
        DateTime expires = Dream.Server.Persistence.SqliteSchemaBootstrapper.ParseUtc((string)select.ExecuteScalar()!);
        Assert.True(expires > DateTime.UtcNow.AddSeconds(30), $"the lease ends {expires:O}");
    }

    [Fact]
    public void ShuttingTheHostDown_ReleasesTheLeasesOfThePlayersStillHere()
    {
        using PersistenceTestDatabase db = new();
        AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        harness.JoinAccepted("c1", "s1", Alice);
        Assert.Equal(1, Leases(db));

        harness.Dispose();

        Assert.Equal(0, Leases(db));
    }

    [Fact]
    public void ARefusedJoin_DoesNotLeaveTheLeaseBehind()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness first = AccountHostHarness.OverSqlite(db);
        first.JoinAccepted("c1", "s1", Alice);
        first.Leave("c1");
        Assert.Equal(0, Leases(db));

        // A stranger asks for Alice: the character belongs to someone else, so no lease is taken.
        string refusal = first.JoinRejectionOf("x1", "sx", Alice, TestAccountTokens.For("Not Alice"));

        Assert.Equal(JoinRejectionReasons.CharacterOwned, refusal);
        Assert.Equal(0, Leases(db));
    }

    [Fact]
    public void ANewCharacter_GetsItsLease_WhenTheJoinIsAccepted()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);

        harness.JoinAccepted("c1", "s1", "Brand New");

        Assert.Equal(1, Leases(db));
        Assert.Equal(1, db.Count("SELECT COUNT(*) FROM character_presence WHERE player_name = 'Brand New';"));
    }

    [Fact]
    public void TheRepositorySql_ClaimsRenewsAndReleasesOnlyForTheHoldingWorld()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness first = AccountHostHarness.OverSqlite(db);
        using AccountHostHarness second = AccountHostHarness.OverSqlite(db, world: "Second");
        SqliteAccountRepository accounts = new(db.Schema);
        WorldId one = first.Runtime.World.Id;
        WorldId two = second.Runtime.World.Id;

        Assert.True(accounts.TryClaimPresence("hero", one, Now, Now.AddSeconds(45)));
        Assert.True(accounts.TryClaimPresence("hero", one, Now.AddSeconds(1), Now.AddSeconds(46)));
        Assert.False(accounts.TryClaimPresence("hero", two, Now.AddSeconds(2), Now.AddSeconds(47)));

        // Renewal and release by a world that does not hold the lease change nothing.
        accounts.RenewPresence(["hero"], two, Now.AddDays(1));
        accounts.ReleasePresence("hero", two);
        Assert.Equal(1, Leases(db));
        Assert.False(accounts.TryClaimPresence("hero", two, Now.AddSeconds(3), Now.AddSeconds(48)));

        // Past the expiry the other world takes it.
        Assert.True(accounts.TryClaimPresence("hero", two, Now.AddSeconds(60), Now.AddSeconds(105)));
        Assert.Equal(two.Value, db.Count("SELECT world_id FROM character_presence WHERE player_name = 'hero';"));

        accounts.ReleasePresence("hero", two);
        Assert.Equal(0, Leases(db));
    }

    [Fact]
    public void AHostWithoutAccounts_TakesNoLeases()
    {
        using PersistenceTestDatabase db = new();
        Dream.Server.DedicatedServerHost host = AccountHostHarness.CreateProductionHost(db, accounts: null);
        using (host)
        {
            host.HandleClientMessage("c1", new ClientJoinRequestMessage("s1", "Plain"));
            for (int i = 0; i < 4; i++)
            {
                host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
            }
        }

        Assert.Equal(0, Leases(db));
    }
}
