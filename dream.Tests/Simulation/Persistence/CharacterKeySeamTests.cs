using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Tests.Server.Persistence;
using Dream.Tests.Support;

namespace Dream.Tests.Simulation.Persistence;

// Wave G (G2, G-D7): persistence keys on CharacterKey, compared ordinally. After migration 0007 "Bob"
// (display "Bob") and "bob" (display "bob~2") are two characters; nothing in memory may merge them.
public sealed class CharacterKeySeamTests
{
    private static string TokenOf(string who) => TestAccountTokens.For(who);

    private static AccountKey AccountOf(string who) => TestAccountTokens.KeyFor(who);

    private static void SeedBobAndBobTilde2(PersistenceTestDatabase database, CountingAccountRepository accounts)
    {
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        PersistedPlayerProfile bob = profiles.GetOrCreate("Bob");
        PersistedPlayerProfile lower = PersistedPlayerProfile.CreateDefault("bob", DateTime.UtcNow);
        profiles.Save(lower with { DisplayName = "bob~2" });
        accounts.AddCharacter("Bob", "Bob", AccountOf("owner"));
        accounts.AddCharacter("bob", "bob~2", AccountOf("owner"));
        Assert.Equal("Bob", bob.PlayerName);
    }

    [Fact]
    public void BobAndBobTilde2_RunAtOnce_WithSeparateKeysInventoriesAndRows()
    {
        using PersistenceTestDatabase database = new();
        CountingAccountRepository accounts = new();
        SeedBobAndBobTilde2(database, accounts);
        using AccountHostHarness harness = new(database, accounts);

        harness.JoinAccepted("c-1", "s-1", "Bob", TokenOf("owner"));
        harness.JoinAccepted("c-2", "s-2", "bob~2", TokenOf("owner"));

        ConnectedPlayer upper = Assert.Single(harness.Runtime.Sessions.Players, p => p.SessionId.Value == "s-1");
        ConnectedPlayer lower = Assert.Single(harness.Runtime.Sessions.Players, p => p.SessionId.Value == "s-2");
        Assert.Equal(("Bob", "Bob"), (upper.CharacterKey.Value, upper.DisplayName));
        Assert.Equal(("bob", "bob~2"), (lower.CharacterKey.Value, lower.DisplayName));
        Assert.Equal("Bob", harness.Runtime.TryCaptureInventorySnapshot(upper.SessionId)!.Value.PlayerName);
        Assert.Equal("bob", harness.Runtime.TryCaptureInventorySnapshot(lower.SessionId)!.Value.PlayerName);
        Assert.Equal(2, harness.Runtime.LoadedInventoryCount);

        // Both run through a coalesced checkpoint and a leave without a write failing: every row names a profile.
        PersistenceCheckpoint checkpoint = Assert.IsType<PersistenceCheckpoint>(harness.Runtime.CapturePersistenceCheckpoint(full: true));
        Assert.Equal(["Bob", "bob"], checkpoint.PlayerWorldStates.Select(s => s.PlayerName).Order(StringComparer.Ordinal));
        PersistenceCheckpoint merged = PersistenceCheckpoint.Merge(checkpoint, checkpoint);
        Assert.Equal(2, merged.PlayerVitals.Count);
        harness.Leave("c-1");
        harness.Leave("c-2");
        Assert.Equal(2, database.Count("SELECT COUNT(*) FROM player_world_state;"));
        Assert.Equal(2, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name IN ('Bob', 'bob');"));
        Assert.Equal("bob~2", new SqlitePlayerProfileRepository(database.Schema).TryGet("bob")!.Value.DisplayName);
    }

    [Fact]
    public void TypingBOB_ReachesKeyBob_AndSurvivesLeaveEvictionAndRejoin()
    {
        using PersistenceTestDatabase database = new();
        CountingAccountRepository accounts = new();
        SeedBobAndBobTilde2(database, accounts);
        using AccountHostHarness harness = new(database, accounts);
        harness.Runtime.MaxDepartedProfiles = 1;

        harness.JoinAccepted("c-1", "s-1", "BOB", TokenOf("owner"));
        ConnectedPlayer joined = Assert.Single(harness.Runtime.Sessions.Players, p => p.SessionId.Value == "s-1");
        Assert.Equal(("Bob", "Bob"), (joined.CharacterKey.Value, joined.DisplayName));
        harness.JoinAccepted("c-2", "s-2", "bob~2", TokenOf("owner"));
        harness.Leave("c-1");
        harness.Leave("c-2");
        // One departed profile is kept, the older was evicted.
        Assert.Equal(1, harness.Runtime.DepartedProfileCount);

        harness.JoinAccepted("c-3", "s-3", "bOb", TokenOf("owner"));
        harness.JoinAccepted("c-4", "s-4", "BOB~2", TokenOf("owner"));

        Assert.Equal("Bob", Assert.Single(harness.Runtime.Sessions.Players, p => p.SessionId.Value == "s-3").CharacterKey.Value);
        Assert.Equal("bob", Assert.Single(harness.Runtime.Sessions.Players, p => p.SessionId.Value == "s-4").CharacterKey.Value);
    }

    [Fact]
    public void TheTrustedTwoArgumentJoin_ResolvesTheKeyThroughTheLookup_AndRecordsNothing()
    {
        using PersistenceTestDatabase database = new();
        CountingAccountRepository accounts = new();
        SeedBobAndBobTilde2(database, accounts);
        using AccountHostHarness harness = new(database, accounts);

        // In process, no token: typing "bOb" reaches the row "Bob", and no account or claim is written.
        harness.RuntimeHost.JoinSession("s-trusted", "bOb");

        ConnectedPlayer player = Assert.Single(harness.Runtime.Sessions.Players, p => !p.IsLocal && p.IsSpawned);
        Assert.Equal(("Bob", "Bob"), (player.CharacterKey.Value, player.DisplayName));
        Assert.True(harness.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, accounts.RecordJoinCalls);
    }

    [Fact]
    public void HostsWithoutAccounts_KeepTheTypedNameAsTheKey()
    {
        using PersistenceTestDatabase database = new();
        using Dream.Server.DedicatedServerHost host = AccountHostHarness.CreateProductionHost(database, accounts: null);
        Assert.False(host.Runtime!.HasAccounts);

        host.HandleClientMessage("c-1", new Dream.Multiplayer.Protocol.ClientJoinRequestMessage("s-1", "  Plain  "));
        Assert.True(host.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
        host.AdvanceTime(Dream.Simulation.Runtime.SimulationClock.FixedDeltaSeconds);

        ConnectedPlayer player = Assert.Single(host.Runtime.Sessions.Players, p => !p.IsLocal && p.IsSpawned);
        Assert.Equal(("Plain", "Plain"), (player.CharacterKey.Value, player.DisplayName));
    }
}
