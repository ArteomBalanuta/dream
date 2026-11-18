using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Repositories;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.BugHunt;

// Bug hunt, transfer packet, BH-XFER-4: the presence lease was renewed on a tick counter but expires on
// the wall clock, and the renewal's result was thrown away. A host whose ticks fall behind real time
// therefore lost the lease without noticing, and the same character could be played in two worlds at
// once -- both writing the one global player_profiles row.
public sealed class XferPresenceRenewalTests
{
    private const string Alice = "Xfer Renew Alice";
    private const string Bob = "Xfer Renew Bob";

    private static long Leases(PersistenceTestDatabase db) => db.Count("SELECT COUNT(*) FROM character_presence;");

    private static void RunSql(PersistenceTestDatabase db, string sql)
    {
        using SqliteConnection connection = db.Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static DateTime ExpiryOf(PersistenceTestDatabase db)
    {
        using SqliteConnection connection = db.Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT expires_utc FROM character_presence;";
        return SqliteSchemaBootstrapper.ParseUtc((string)command.ExecuteScalar()!);
    }

    // The renewal cadence has to be shorter than the expiry in the unit the expiry uses, with room to
    // spare: this is the invariant the tick-counter cadence could not state at all.
    [Fact]
    public void TheRenewalCadence_IsWellInsideTheLease_InTheUnitTheLeaseExpiresOn()
    {
        Assert.True(
            RuntimePersistenceBridge.PresenceRenewInterval * 3 <= RuntimePersistenceBridge.PresenceTimeToLive,
            "the renewal must leave room for two lost renewals inside the lease");
        Assert.True(RuntimePersistenceBridge.PresenceRenewInterval > TimeSpan.Zero);

        // The pump that looks at that wall clock has to be far quicker than the renewal itself, so a host
        // running well below real time still asks in time.
        TimeSpan pump = TimeSpan.FromSeconds(
            WorldSessionRuntime.PresencePumpIntervalTicks / (double)SimulationClock.AuthoritativeTicksPerSecond);
        Assert.True(pump * 10 <= RuntimePersistenceBridge.PresenceRenewInterval, $"the pump is {pump}");
    }

    // The renewal reports what it could not renew instead of discarding the row count.
    [Fact]
    public void TheRepository_ReportsTheKeysItCouldNotRenew()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness first = AccountHostHarness.OverSqlite(db);
        using AccountHostHarness second = AccountHostHarness.OverSqlite(db, world: "Second");
        SqliteAccountRepository accounts = new(db.Schema);
        WorldId one = first.Runtime.World.Id;
        WorldId two = second.Runtime.World.Id;
        DateTime now = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(accounts.TryClaimPresence("held", one, now, now.AddSeconds(45)));
        Assert.True(accounts.TryClaimPresence("taken", two, now, now.AddSeconds(45)));

        List<string> lost = [];
        accounts.RenewPresence(["held", "taken", "never-claimed"], one, now.AddSeconds(45), lost);

        Assert.Equal(["taken", "never-claimed"], lost);
    }

    // BH-XFER-4. This world's lease ran out while it was stalled and another world took the character
    // over. The seat here is not the owner any more, so it must not play on writing the global profile
    // row: the renewal notices and the host closes that connection.
    [Fact]
    public void AHostThatLostTheLeaseUnderAPlayer_ClosesThatConnection()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness first = AccountHostHarness.OverSqlite(db);
        using AccountHostHarness second = AccountHostHarness.OverSqlite(db, world: "Second");
        first.JoinAccepted("c1", "s1", Alice);
        Assert.Equal(1, Leases(db));

        // The stall: nothing renewed the lease in time, so it ran out and the other world claimed it.
        RunSql(db, "UPDATE character_presence SET expires_utc = '2000-01-01T00:00:00.0000000Z';");
        second.JoinAccepted("d1", "t1", Alice);
        Assert.Equal(second.Runtime.World.Id.Value, db.Count("SELECT world_id FROM character_presence;"));

        for (int i = 0; i < 400 && first.Host.IsConnectionJoined("c1"); i++)
        {
            first.Tick();
        }

        Assert.False(first.Host.IsConnectionJoined("c1"));
        Assert.Equal(DedicatedServerHost.PresenceLostText, first.Host.TakeCloseReason("c1"));

        // The other world still holds the character: the loser's cleanup is world-scoped.
        Assert.Equal(1, Leases(db));
        Assert.Equal(second.Runtime.World.Id.Value, db.Count("SELECT world_id FROM character_presence;"));
        Assert.True(second.Host.IsConnectionJoined("d1"));
    }

    // Control: a healthy host keeps its lease and its player. Nothing is closed, and the expiry keeps
    // moving forward as the renewals land.
    [Fact]
    public void AHealthyHost_KeepsItsLease_AndItsPlayer()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        harness.JoinAccepted("c1", "s1", Alice);

        // Shorten the lease so a renewal has to be the thing that pushes it back out again.
        RunSql(
            db,
            $"UPDATE character_presence SET expires_utc = '{SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow.AddSeconds(3))}';");

        for (int i = 0; i < 200; i++)
        {
            harness.Tick();
        }

        Assert.True(harness.Host.IsConnectionJoined("c1"));
        Assert.Equal(1, Leases(db));
        Assert.Equal(harness.Runtime.World.Id.Value, db.Count("SELECT world_id FROM character_presence;"));
        Assert.True(ExpiryOf(db) > DateTime.UtcNow.AddSeconds(30), "the lease was not renewed");
    }

    // Control: losing one character's lease closes only that connection. A second player in the same
    // world, whose lease is intact, plays on.
    [Fact]
    public void LosingOneLease_LeavesTheOtherPlayersInTheWorldAlone()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness first = AccountHostHarness.OverSqlite(db);
        using AccountHostHarness second = AccountHostHarness.OverSqlite(db, world: "Second");
        first.JoinAccepted("c1", "s1", Alice);
        first.JoinAccepted("c2", "s2", Bob);
        Assert.Equal(2, Leases(db));

        // Only Alice's lease runs out and is taken over.
        RunSql(db, "UPDATE character_presence SET expires_utc = '2000-01-01T00:00:00.0000000Z' WHERE player_name NOT LIKE '%Bob%';");
        second.JoinAccepted("d1", "t1", Alice);

        for (int i = 0; i < 400 && first.Host.IsConnectionJoined("c1"); i++)
        {
            first.Tick();
        }

        Assert.False(first.Host.IsConnectionJoined("c1"));
        Assert.True(first.Host.IsConnectionJoined("c2"));
    }
}
