using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Characters;
using Dream.Tests.Support;

namespace Dream.Tests.Server;

// Wave G (G2): takeover on a host with accounts. The account decides who may take what; silence does
// not. Hosts without accounts keep the 8 s window (StaleSessionTakeoverTests is their regression test).
public sealed class CharacterTakeoverTests
{
    private static string TokenOf(string who) => TestAccountTokens.For(who);

    [Fact]
    public void SameAccount_TakesOverALiveHolderAtOnce_KeepsItsProfile_AndLeavesNoGhost()
    {
        using AccountHostHarness harness = new();
        harness.JoinAccepted("old", "s-old", "Hero", TokenOf("me"));
        ConnectedPlayer first = Assert.Single(harness.Runtime.Sessions.Players, p => !p.IsLocal && p.IsSpawned);
        Assert.NotNull(harness.Runtime.TryCaptureInventorySnapshot(first.SessionId));
        // The old connection is live: it was heard from a moment ago.
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        IServerToClientMessage reply = harness.Join("new", "s-new", "Hero", TokenOf("me"));

        Assert.IsType<JoinAcceptedMessage>(reply);
        Assert.False(harness.Host.TryGetConnectionContext("old", out _));
        Assert.True(harness.Host.TryTakeTakenOverConnection(out string closed));
        Assert.Equal("old", closed);
        ConnectedPlayer seated = Assert.Single(harness.Runtime.Sessions.Players, p => !p.IsLocal && p.IsSpawned);
        Assert.Equal("s-new", seated.SessionId.Value);
        Assert.Equal("Hero", seated.CharacterKey.Value);
        // The row survived the takeover (profile saved by the old seat's release, loaded for the new one).
        Assert.Equal(1, harness.Database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'Hero';"));
        Assert.Equal(1, harness.Runtime.Sessions.Players.Count(p => !p.IsLocal && p.IsSpawned));
    }

    [Fact]
    public void SameAccount_WithTheSameSessionId_TakesOverAtOnce()
    {
        using AccountHostHarness harness = new();
        harness.JoinAccepted("old", "s-1", "Hero", TokenOf("me"));

        IServerToClientMessage reply = harness.Join("new", "s-1", "Hero", TokenOf("me"));

        Assert.IsType<JoinAcceptedMessage>(reply);
        Assert.True(harness.Host.TryTakeTakenOverConnection(out string closed));
        Assert.Equal("old", closed);
        Assert.True(harness.Host.IsConnectionJoined("new"));
    }

    [Fact]
    public void OtherAccount_WithTheVictimsSessionId_IsRejected_LiveAndAfterEightSeconds()
    {
        using AccountHostHarness harness = new();
        harness.JoinAccepted("victim", "s-victim", "Victim", TokenOf("victim"));

        string live = harness.JoinRejectionOf("hijack-1", "s-victim", "Other Name", TokenOf("hijacker"));
        harness.Clock.Advance(DedicatedServerHost.TakeoverSilence + TimeSpan.FromSeconds(1));
        string stale = harness.JoinRejectionOf("hijack-2", "s-victim", "Other Name", TokenOf("hijacker"));
        string sameName = harness.JoinRejectionOf("hijack-3", "s-victim", "Victim", TokenOf("hijacker"));

        Assert.Equal(DedicatedServerHost.SessionInUseRejectionReason, live);
        Assert.Equal(DedicatedServerHost.SessionInUseRejectionReason, stale);
        Assert.Equal(JoinRejectionReasons.CharacterOwned, sameName);
        Assert.True(harness.Host.IsConnectionJoined("victim"));
        Assert.False(harness.Host.TryTakeTakenOverConnection(out _));
        Assert.Equal("Victim", Assert.Single(harness.Runtime.Sessions.Players, p => !p.IsLocal && p.IsSpawned).CharacterKey.Value);
    }

    [Fact]
    public void ACharacterJustReleasedToNobody_HeldByAnotherAccount_IsInUse()
    {
        using AccountHostHarness harness = new();
        harness.Accounts.AddCharacter("Shared", "Shared", AccountKeyOf("first"));
        harness.JoinAccepted("first", "s-1", "Shared", TokenOf("first"));
        // An admin released the character while its player is still connected.
        harness.Accounts.AddCharacter("Shared", "Shared", owner: null);

        string reason = harness.JoinRejectionOf("second", "s-2", "Shared", TokenOf("second"));

        Assert.Equal(JoinRejectionReasons.CharacterInUse, reason);
        Assert.True(harness.Host.IsConnectionJoined("first"));
    }

    [Fact]
    public void ADepartedSessionIdReusedByAnotherAccount_RestoresOnlyThatAccountsCharacter()
    {
        using AccountHostHarness harness = new();
        harness.JoinAccepted("c-a", "s-shared", "Alpha", TokenOf("alpha"));
        harness.Leave("c-a");

        harness.JoinAccepted("c-b", "s-shared", "Beta", TokenOf("beta"));

        ConnectedPlayer player = Assert.Single(harness.Runtime.Sessions.Players, p => !p.IsLocal && p.IsSpawned);
        Assert.Equal("Beta", player.CharacterKey.Value);
        Assert.Equal("Beta", player.DisplayName);
        Assert.Equal("Beta", harness.Runtime.TryCaptureInventorySnapshot(player.SessionId)!.Value.PlayerName);
        Assert.Equal(1, harness.Database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'Alpha';"));
    }

    [Fact]
    public void AJoinedConnection_SendingAJoinForAnotherCharacter_GetsAlreadyJoined_AndItsSeatKeepsItsKey()
    {
        using AccountHostHarness harness = new();
        harness.JoinAccepted("c1", "s1", "First", TokenOf("me"));

        string reason = harness.JoinRejectionOf("c1", "s1", "Second", TokenOf("me"));

        Assert.Equal(JoinRejectionReasons.AlreadyJoined, reason);
        ConnectedPlayer player = Assert.Single(harness.Runtime.Sessions.Players, p => !p.IsLocal && p.IsSpawned);
        Assert.Equal("First", player.CharacterKey.Value);
        Assert.Equal("First", player.DisplayName);
        Assert.True(harness.Host.IsConnectionJoined("c1"));
    }

    [Fact]
    public void ASpammerOnTheOwnersAddress_IsThrottled_ButNeverBlocksTheOwner()
    {
        using AccountHostHarness harness = new();
        harness.Accounts.AddCharacter("Mine", "Mine", AccountKeyOf("owner"));
        Assert.Equal(
            JoinRejectionReasons.CharacterOwned,
            harness.JoinRejectionOf("10.0.0.7:4000", "s-spam-0", "Mine", TokenOf("spammer")));
        for (int i = 1; i < 3; i++)
        {
            // Throttled before any prefetch: the lookup count does not move.
            int lookups = harness.Accounts.LookupCalls;
            string reason = harness.JoinRejectionOf($"10.0.0.7:{4000 + i}", $"s-spam-{i}", "Mine", TokenOf("spammer"));
            Assert.StartsWith(DedicatedServerHost.JoinThrottledRejectionReason, reason);
            Assert.Equal(lookups, harness.Accounts.LookupCalls);
        }

        Assert.True(harness.Host.IsJoinThrottled("10.0.0.7:4100", "s-spam-x", "Mine", AccountKeyOf("spammer")));
        Assert.False(harness.Host.IsJoinThrottled("10.0.0.7:4101", "s-owner", "Mine", AccountKeyOf("owner")));

        IServerToClientMessage ownerJoin = harness.Join("10.0.0.7:4200", "s-owner", "Mine", TokenOf("owner"));
        Assert.IsType<JoinAcceptedMessage>(ownerJoin);
    }

    private static AccountKey AccountKeyOf(string who) => TestAccountTokens.KeyFor(who);
}
