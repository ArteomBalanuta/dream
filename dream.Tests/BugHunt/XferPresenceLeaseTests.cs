using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Support;
using Xunit;

namespace Dream.Tests.BugHunt;

// Bug hunt, transfer packet: the character presence lease is claimed by a join's prefetch, so every path
// that gives up on that join has to let the lease go again. Covers BH-XFER-1 (the connection dropped
// before the tick routed the join), BH-XFER-2 (a ban found at routing time, same release point) and
// BH-XFER-3 (a join rejected while its load was still in flight), with the controls around them.
public sealed class XferPresenceLeaseTests
{
    private const string Alice = "Xfer Alice";
    private const string Bob = "Xfer Bob";

    private static long Leases(PersistenceTestDatabase db) => db.Count("SELECT COUNT(*) FROM character_presence;");

    // Polls instead of flushing the write queue: a mid-test Flush starves the neighbouring test classes.
    private static void Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 1500 && !condition(); i++)
        {
            Thread.Sleep(10);
        }

        Assert.True(condition(), $"timed out waiting for {what}");
    }

    private static void Settles(Func<bool> condition, string what)
    {
        for (int i = 0; i < 50; i++)
        {
            Assert.True(condition(), what);
            Thread.Sleep(10);
        }
    }

    // Gives the character a row of its own (and an owner), then leaves with the lease released.
    private static void CreateCharacter(AccountHostHarness harness, string name, string connectionId, string sessionId)
    {
        harness.JoinAccepted(connectionId, sessionId, name);
        harness.Leave(connectionId);
    }

    // BH-XFER-1: the prefetch claimed the lease, then the client's socket dropped before the tick routed
    // the join. Nothing was ever seated here, so the lease must not survive the drop.
    [Fact]
    public void AJoinWhoseConnectionDropsBeforeItIsRouted_LeavesNoLeaseBehind()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        CreateCharacter(harness, Alice, "c1", "s1");
        Assert.Equal(0, Leases(db));

        harness.SendJoin("c2", "s2", Alice, TestAccountTokens.For(Alice));
        Until(() => Leases(db) == 1, "the prefetch to claim the lease");

        harness.Host.ReleaseConnection("c2");
        harness.Tick();

        Assert.Equal(0, Leases(db));
    }

    // BH-XFER-1, the self-renewing form: a client that reconnect-loops against this world kept refreshing
    // the 45 s lockout, so no other world could ever take the character.
    [Fact]
    public void AReconnectLoopOfAbandonedJoins_LeavesNoLeaseBehind()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        CreateCharacter(harness, Alice, "c1", "s1");

        for (int i = 0; i < 3; i++)
        {
            string connection = $"loop{i}";
            harness.SendJoin(connection, $"loop-s{i}", Alice, TestAccountTokens.For(Alice));
            Until(() => Leases(db) == 1, "the prefetch to claim the lease");
            harness.Host.ReleaseConnection(connection);
            harness.Tick();
            Assert.Equal(0, Leases(db));
        }
    }

    // BH-XFER-1 control: a join that is actually routed keeps its lease while the player is here, and the
    // detach releases it. The abandoned-join release must not fire on the accepted path.
    [Fact]
    public void ANormalJoin_KeepsItsLease_UntilThePlayerLeaves()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);

        harness.JoinAccepted("c1", "s1", Alice);
        Assert.Equal(1, Leases(db));

        // Still held several ticks into the session.
        for (int i = 0; i < 4; i++)
        {
            harness.Tick();
        }

        Assert.Equal(1, Leases(db));

        harness.Leave("c1");
        Assert.Equal(0, Leases(db));
    }

    // BH-XFER-1 control: the same connection dropping must not take away a lease the character is holding
    // through a *different*, live connection in this world (the takeover shape).
    [Fact]
    public void AnAbandonedJoin_DoesNotTakeTheLeaseOfTheSameCharacterAlreadySeatedHere()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db);
        harness.JoinAccepted("c1", "s1", Alice);
        Assert.Equal(1, Leases(db));

        // A second connection asks for the same character, then gives up before the tick routes it.
        harness.SendJoin("c2", "s2", Alice, TestAccountTokens.For(Alice));
        harness.Host.ReleaseConnection("c2");
        harness.Tick();

        Assert.Equal(1, Leases(db));
        Assert.True(harness.Host.IsConnectionJoined("c1"));
    }

    // BH-XFER-1 control: a different character is unaffected, and a second world may still take it.
    [Fact]
    public void AnAbandonedJoin_DoesNotStopADifferentCharacterJoiningAnotherWorld()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness first = AccountHostHarness.OverSqlite(db);
        using AccountHostHarness second = AccountHostHarness.OverSqlite(db, world: "Second");
        CreateCharacter(first, Alice, "c1", "s1");

        first.SendJoin("c2", "s2", Alice, TestAccountTokens.For(Alice));
        Until(() => Leases(db) == 1, "the prefetch to claim the lease");
        first.Host.ReleaseConnection("c2");
        first.Tick();

        second.JoinAccepted("d1", "t1", Bob);

        Assert.Equal(1, Leases(db));
        Assert.Equal(1, db.Count($"SELECT COUNT(*) FROM character_presence WHERE world_id = {second.Runtime.World.Id.Value};"));
    }

    // BH-XFER-1 control: the character is free for another world the moment the abandoned join is cleaned
    // up, instead of waiting out the 45 s lease.
    [Fact]
    public void AfterAnAbandonedJoin_AnotherWorldTakesTheCharacterAtOnce()
    {
        using PersistenceTestDatabase db = new();
        using AccountHostHarness first = AccountHostHarness.OverSqlite(db);
        using AccountHostHarness second = AccountHostHarness.OverSqlite(db, world: "Second");
        CreateCharacter(first, Alice, "c1", "s1");

        first.SendJoin("c2", "s2", Alice, TestAccountTokens.For(Alice));
        Until(() => Leases(db) == 1, "the prefetch to claim the lease");
        first.Host.ReleaseConnection("c2");
        first.Tick();
        Assert.Equal(0, Leases(db));

        IServerToClientMessage reply = second.Join("d1", "t1", Alice, TestAccountTokens.For(Alice));
        Assert.True(reply is JoinAcceptedMessage, $"the second world refused: {(reply as JoinRejectedMessage)?.Reason}");

        Assert.Equal(1, Leases(db));
        Assert.Equal(second.Runtime.World.Id.Value, db.Count("SELECT world_id FROM character_presence;"));
    }

    // BH-XFER-3: the join waited out MaxJoinPrefetchDeferralTicks with its load still queued and was
    // rejected; the load then ran. The rejection had already taken the prefetch out of the registry, so a
    // lease claimed now would be unreachable by every release path. The load must not claim one.
    [Fact]
    public void AJoinRejectedWhileItsLoadIsStillInFlight_NeverLeavesAnUnreachableLease()
    {
        using PersistenceTestDatabase db = new();
        GatedAccounts? gate = null;
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db, wrap: inner => gate = new GatedAccounts(inner));
        Assert.NotNull(gate);
        CreateCharacter(harness, Alice, "c1", "s1");
        Assert.Equal(0, Leases(db));

        gate!.Block();
        int lookupsBefore = gate.Lookups;
        harness.SendJoin("c2", "s2", Alice, TestAccountTokens.For(Alice));
        Until(() => gate.Waiting, "the prefetch's lookup to reach the gate");

        // Not harness.Tick(): it flushes the write queue, and the worker is parked inside the lookup.
        JoinRejectedMessage? rejected = null;
        List<PendingConnectionReply> replies = [];
        for (int i = 0; i < DedicatedServerHost.MaxJoinPrefetchDeferralTicks + 8 && rejected == null; i++)
        {
            harness.Host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
            harness.Host.DrainPendingJoinRepliesInto(replies);
            foreach (PendingConnectionReply reply in replies)
            {
                rejected ??= reply.Message as JoinRejectedMessage;
            }
        }

        Assert.NotNull(rejected);

        // The load finally runs, long after its join was given up on.
        gate.Release();
        Until(() => gate.Lookups > lookupsBefore, "the parked lookup to finish");
        Settles(() => Leases(db) == 0, "the abandoned load must claim no lease");
    }

    // BH-XFER-3 control: the same gate, released before the deferral runs out, still joins normally and
    // still claims the lease. The skip must key on the join being abandoned, not on the load being slow.
    [Fact]
    public void ASlowLoadThatStillArrivesInTime_ClaimsTheLeaseAsUsual()
    {
        using PersistenceTestDatabase db = new();
        GatedAccounts? gate = null;
        using AccountHostHarness harness = AccountHostHarness.OverSqlite(db, wrap: inner => gate = new GatedAccounts(inner));
        Assert.NotNull(gate);
        CreateCharacter(harness, Alice, "c1", "s1");

        gate!.Block();
        harness.SendJoin("c2", "s2", Alice, TestAccountTokens.For(Alice));
        Until(() => gate.Waiting, "the prefetch's lookup to reach the gate");
        for (int i = 0; i < 16; i++)
        {
            harness.Host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        }

        gate.Release();
        for (int i = 0; i < 8 && !harness.Host.IsConnectionJoined("c2"); i++)
        {
            harness.Tick();
        }

        Assert.True(harness.Host.IsConnectionJoined("c2"));
        Assert.Equal(1, Leases(db));
    }

    // Holds the prefetch's account lookup on the persistence worker, so a join can be rejected for taking
    // too long while its own load is still in flight.
    private sealed class GatedAccounts(IAccountRepository inner) : IAccountRepository
    {
        private readonly ManualResetEventSlim _gate = new(initialState: true);
        private int _lookups;
        private int _waiting;

        public int Lookups => Volatile.Read(ref _lookups);

        public bool Waiting => Volatile.Read(ref _waiting) > 0;

        public void Block() => _gate.Reset();

        public void Release() => _gate.Set();

        public CharacterLookup Lookup(string trimmedName, AccountKey? account)
        {
            Interlocked.Increment(ref _waiting);
            try
            {
                _gate.Wait(TimeSpan.FromSeconds(60));
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }

            CharacterLookup lookup = inner.Lookup(trimmedName, account);
            Interlocked.Increment(ref _lookups);
            return lookup;
        }

        public bool RecordJoin(AccountKey account, CharacterKey character, bool claim, DateTime utcNow) =>
            inner.RecordJoin(account, character, claim, utcNow);

        public IReadOnlyList<AccountCharacterSummary> ListCharacters(AccountKey account, int max) =>
            inner.ListCharacters(account, max);

        public AccountStanding? ReadStanding(AccountKey account) => inner.ReadStanding(account);

        public bool TrySetBan(string displayName, DateTime? untilUtc, string? reason) =>
            inner.TrySetBan(displayName, untilUtc, reason);

        public bool TrySetMute(string displayName, DateTime? untilUtc) => inner.TrySetMute(displayName, untilUtc);

        public bool TrySetRole(string displayName, int role) => inner.TrySetRole(displayName, role);

        public bool TryClaimPresence(string characterKey, WorldId world, DateTime nowUtc, DateTime expiresUtc) =>
            inner.TryClaimPresence(characterKey, world, nowUtc, expiresUtc);

        public void RenewPresence(IReadOnlyList<string> characterKeys, WorldId world, DateTime expiresUtc) =>
            inner.RenewPresence(characterKeys, world, expiresUtc);

        public void ReleasePresence(string characterKey, WorldId world) => inner.ReleasePresence(characterKey, world);
    }
}
