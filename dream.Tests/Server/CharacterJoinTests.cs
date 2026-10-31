using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Characters;
using Dream.Tests.Support;

namespace Dream.Tests.Server;

// Wave G (G2): the join path on a host with accounts. Ownership is decided on the tick from memory
// (G-D1), the prefetch only reads (G-D2), and a refused join writes nothing.
public sealed class CharacterJoinTests
{
    private static string TokenOf(string who) => TestAccountTokens.For(who);

    private static AccountKey AccountOf(string who) => TestAccountTokens.KeyFor(who);

    [Fact]
    public void NullToken_IsRejectedWithTokenRequired_BeforeAnyPrefetch_AndSeatsNobody()
    {
        using AccountHostHarness harness = new();

        string reason = harness.JoinRejectionOf("c1", "s1", "Alice", token: null);

        Assert.Equal(JoinRejectionReasons.TokenRequired, reason);
        Assert.Equal(0, harness.Accounts.LookupCalls);
        Assert.False(harness.Host.IsConnectionJoined("c1"));
        Assert.DoesNotContain(harness.Runtime.Sessions.Players, player => !player.IsLocal);
        Assert.Equal(0, harness.Database.Count("SELECT COUNT(*) FROM player_profiles;"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    public void MalformedToken_IsRejectedWithTokenInvalid_BeforeAnyPrefetch(string token)
    {
        using AccountHostHarness harness = new();

        string reason = harness.JoinRejectionOf("c1", "s1", "Alice", token);

        Assert.Equal(JoinRejectionReasons.TokenInvalid, reason);
        Assert.Equal(0, harness.Accounts.LookupCalls);
        Assert.Equal(0, harness.Accounts.AccountCount);
        Assert.False(harness.Host.IsConnectionJoined("c1"));
    }

    [Fact]
    public void BlankName_IsRejectedWithNameInvalid_OnAnAccountHost()
    {
        using AccountHostHarness harness = new();

        Assert.Equal(JoinRejectionReasons.NameInvalid, harness.JoinRejectionOf("c1", "s1", "   ", TokenOf("a")));
        Assert.Equal(0, harness.Accounts.LookupCalls);
    }

    [Fact]
    public void NewCharacter_IsCreatedAndOwnedByTheJoiningToken()
    {
        using AccountHostHarness harness = new();

        harness.JoinAccepted("c1", "s1", "Alice");

        Assert.Equal(AccountOf("Alice"), harness.Accounts.OwnerOf("Alice"));
        Assert.Equal(1, harness.Database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'Alice';"));
        Assert.Equal(1, harness.Accounts.AccountCount);
    }

    [Fact]
    public void TwoAccountsOneName_SecondIsRejectedCharacterOwned_AcrossTicks_AndTheHolderKeepsItsSeat()
    {
        using AccountHostHarness harness = new();
        harness.JoinAccepted("owner", "s-owner", "Shared", TokenOf("owner"));
        int actorId = harness.Runtime.Sessions.Players.Single(p => !p.IsLocal && p.IsSpawned).ActorId;

        string reason = harness.JoinRejectionOf("stranger", "s-stranger", "Shared", TokenOf("stranger"));
        string again = harness.JoinRejectionOf("stranger-2", "s-stranger-2", "shared", TokenOf("stranger"));

        Assert.Equal(JoinRejectionReasons.CharacterOwned, reason);
        Assert.Equal(JoinRejectionReasons.CharacterOwned, again);
        Assert.True(harness.Host.IsConnectionJoined("owner"));
        ConnectedPlayer holder = Assert.Single(harness.Runtime.Sessions.Players, p => !p.IsLocal && p.IsSpawned);
        Assert.Equal("s-owner", holder.SessionId.Value);
        Assert.Equal(actorId, holder.ActorId);
        Assert.False(harness.Host.TryTakeTakenOverConnection(out _));
        Assert.Equal(AccountOf("owner"), harness.Accounts.OwnerOf("Shared"));
    }

    [Theory]
    [InlineData("s-a", "s-b")]
    [InlineData("s-b", "s-a")]
    public void TwoAccountsOneNameInTheSameBatch_ExactlyOneIsAccepted_WhicheverSessionIdOrder(string firstOwnerSession, string secondSession)
    {
        using AccountHostHarness harness = new();
        // Joins apply in ordinal session-id order, so the lower session id owns the name.
        harness.SendJoin("c-1", firstOwnerSession, "Contested", TokenOf("one"));
        harness.SendJoin("c-2", secondSession, "Contested", TokenOf("two"));

        for (int i = 0; i < 8; i++)
        {
            harness.Tick();
        }

        IServerToClientMessage first = Assert.Single(harness.RepliesOf("c-1"));
        IServerToClientMessage second = Assert.Single(harness.RepliesOf("c-2"));
        bool oneWon = first is JoinAcceptedMessage;
        Assert.NotEqual(oneWon, second is JoinAcceptedMessage);
        string winnerSession = string.CompareOrdinal(firstOwnerSession, secondSession) < 0 ? firstOwnerSession : secondSession;
        Assert.Equal(oneWon ? firstOwnerSession : secondSession, winnerSession);
        JoinRejectedMessage loser = Assert.IsType<JoinRejectedMessage>(oneWon ? second : first);
        Assert.Equal(JoinRejectionReasons.CharacterOwned, loser.Reason);
        Assert.Equal(AccountOf(oneWon ? "one" : "two"), harness.Accounts.OwnerOf("Contested"));
    }

    [Fact]
    public void LegacyRow_IsClaimedByTheFirstToken_AndALaterTokenIsRejected()
    {
        using AccountHostHarness harness = new();
        harness.Accounts.AddCharacter("Legacy", "Legacy");

        harness.JoinAccepted("c1", "s1", "legacy", TokenOf("first"));
        harness.Leave("c1");
        string reason = harness.JoinRejectionOf("c2", "s2", "Legacy", TokenOf("second"));

        Assert.Equal(JoinRejectionReasons.CharacterOwned, reason);
        Assert.Equal(AccountOf("first"), harness.Accounts.OwnerOf("Legacy"));
    }

    [Fact]
    public void ReleasedRow_CanBeClaimedByAnotherToken()
    {
        using AccountHostHarness harness = new();
        harness.Accounts.AddCharacter("Moved", "Moved", AccountOf("old"));
        Assert.Equal(JoinRejectionReasons.CharacterOwned, harness.JoinRejectionOf("c1", "s1", "Moved", TokenOf("new")));

        harness.Accounts.AddCharacter("Moved", "Moved", owner: null);
        harness.JoinAccepted("c2", "s2", "Moved", TokenOf("new"));

        Assert.Equal(AccountOf("new"), harness.Accounts.OwnerOf("Moved"));
    }

    [Fact]
    public void SixthCharacter_IsRejectedWithCharacterLimit_AndWritesNoRow()
    {
        using AccountHostHarness harness = new();
        AccountKey account = AccountOf("collector");
        for (int i = 0; i < CharacterNames.MaxCharactersPerAccount; i++)
        {
            harness.Accounts.AddCharacter($"Owned{i}", $"Owned{i}", account);
        }

        harness.Accounts.AddCharacter("Loose", "Loose");
        string create = harness.JoinRejectionOf("c1", "s1", "Brand New", TokenOf("collector"));
        string claim = harness.JoinRejectionOf("c2", "s2", "Loose", TokenOf("collector"));

        Assert.Equal(JoinRejectionReasons.CharacterLimit, create);
        Assert.Equal(JoinRejectionReasons.CharacterLimit, claim);
        Assert.Equal(0, harness.Database.Count("SELECT COUNT(*) FROM player_profiles;"));
        Assert.Null(harness.Accounts.OwnerOf("Loose"));
        // The characters it owns still join.
        harness.JoinAccepted("c3", "s3", "Owned2", TokenOf("collector"));
    }

    [Fact]
    public void CreatingTwoNewNamesInTheSameBatch_AtFourOwned_AcceptsExactlyOne()
    {
        using AccountHostHarness harness = new();
        AccountKey account = AccountOf("busy");
        for (int i = 0; i < CharacterNames.MaxCharactersPerAccount - 1; i++)
        {
            harness.Accounts.AddCharacter($"Owned{i}", $"Owned{i}", account);
        }

        harness.SendJoin("c-1", "s-1", "NewOne", TokenOf("busy"));
        harness.SendJoin("c-2", "s-2", "NewTwo", TokenOf("busy"));
        for (int i = 0; i < 8; i++)
        {
            harness.Tick();
        }

        IServerToClientMessage[] replies = [Assert.Single(harness.RepliesOf("c-1")), Assert.Single(harness.RepliesOf("c-2"))];
        Assert.Equal(1, replies.Count(reply => reply is JoinAcceptedMessage));
        JoinRejectedMessage rejected = Assert.IsType<JoinRejectedMessage>(Assert.Single(replies, reply => reply is JoinRejectedMessage));
        Assert.Equal(JoinRejectionReasons.CharacterLimit, rejected.Reason);
        Assert.Equal(1, harness.Database.Count("SELECT COUNT(*) FROM player_profiles;"));
    }

    [Fact]
    public void RejectedJoins_WriteNoProfileRowAndNoAccount()
    {
        using AccountHostHarness harness = new();
        harness.Accounts.AddCharacter("Taken", "Taken", AccountOf("owner"));
        int accountsBefore = harness.Accounts.AccountCount;

        Assert.Equal(JoinRejectionReasons.CharacterOwned, harness.JoinRejectionOf("c1", "s1", "Taken", TokenOf("stranger")));
        Assert.Equal(JoinRejectionReasons.NameInvalid, harness.JoinRejectionOf("c2", "s2", new string('x', CharacterNames.MaxNewNameLength + 1), TokenOf("stranger")));

        Assert.Equal(accountsBefore, harness.Accounts.AccountCount);
        Assert.Equal(0, harness.Accounts.RecordJoinCalls);
        Assert.Equal(0, harness.Database.Count("SELECT COUNT(*) FROM player_profiles;"));
    }

    [Fact]
    public void NameRule_AppliesToNewNamesOnly_AFortyCharacterLegacyNameJoinsAndAThirtyThreeCharacterNewOneDoesNot()
    {
        using AccountHostHarness harness = new();
        string legacy = new('L', 40);
        harness.Accounts.AddCharacter(legacy, legacy);

        harness.JoinAccepted("c1", "s1", legacy, TokenOf("legacy-owner"));
        string reason = harness.JoinRejectionOf("c2", "s2", new string('N', 33), TokenOf("legacy-owner"));

        Assert.Equal(JoinRejectionReasons.NameInvalid, reason);
    }

    [Fact]
    public void NewNameEqualToARetiredKey_IsRejectedAsTaken_AndTheRowIsUntouched()
    {
        using AccountHostHarness harness = new();
        // "Bob" is a retired key: the character was renamed, so its display name is now "Robert".
        harness.Accounts.AddCharacter("Bob", "Robert", AccountOf("owner"));

        string reason = harness.JoinRejectionOf("c1", "s1", "Bob", TokenOf("someone"));

        Assert.Equal(JoinRejectionReasons.NameTaken, reason);
        Assert.Equal(AccountOf("owner"), harness.Accounts.OwnerOf("Bob"));
        Assert.Equal(0, harness.Database.Count("SELECT COUNT(*) FROM player_profiles;"));
    }

    [Fact]
    public void PendingJoin_ChangesNothingForTheHolder_UntilItIsApplied()
    {
        using AccountHostHarness harness = new();
        harness.JoinAccepted("owner", "s-owner", "Held", TokenOf("owner"));
        ConnectedPlayer before = Assert.Single(harness.Runtime.Sessions.Players, p => !p.IsLocal && p.IsSpawned);

        harness.SendJoin("stranger", "s-stranger", "Held", TokenOf("stranger"));

        // Received but not applied: no claim, no seat, no reply, and the holder is untouched.
        Assert.Empty(harness.RepliesOf("stranger"));
        Assert.False(harness.Host.IsConnectionJoined("stranger"));
        Assert.True(harness.Host.IsConnectionJoined("owner"));
        ConnectedPlayer after = Assert.Single(harness.Runtime.Sessions.Players, p => !p.IsLocal && p.IsSpawned);
        Assert.Same(before, after);
    }

    [Fact]
    public void TwoTokensOnOneUnownedName_DoNotLivelock_AndTheLaterOneReadsTheNewOwner()
    {
        using AccountHostHarness harness = new();
        harness.SendJoin("c-1", "s-1", "Race", TokenOf("one"));
        harness.SendJoin("c-2", "s-2", "Race", TokenOf("two"));

        int ticks = 0;
        while ((harness.RepliesOf("c-1").Count == 0 || harness.RepliesOf("c-2").Count == 0) && ticks++ < 20)
        {
            harness.Tick();
        }

        Assert.True(ticks < 20, "a join never got its reply");
        Assert.Equal(1, new[] { "c-1", "c-2" }.Count(c => harness.RepliesOf(c).OfType<JoinAcceptedMessage>().Any()));
    }

    [Fact(Skip = "wave G: needs the SQLite account repository in ServerBootstrap's bundle (G1) and two worlds in one database, unskip at glue")]
    public void TheSameCharacter_JoinsWorldOneAndLaterWorldTwo_LevelAndBaseStatsFollow_ItemsPositionAndHealthStayPerWorld_AStrangerIsRejectedInBoth()
    {
        // Glue: world 1 and world 2 of one database, each hosted in turn with the bundle's real
        // SqliteAccountRepository. Level, experience and base stats come from the global profile row; the
        // inventory, world state and health rows are per world; a second token is refused in both worlds.
    }

    [Fact]
    public void RepeatedJoinOfTheSameCharacterOnTheSameConnection_IsIdempotent()
    {
        using AccountHostHarness harness = new();
        harness.JoinAccepted("c1", "s1", "Idem", TokenOf("idem"));

        harness.JoinAccepted("c1", "s1", "idem", TokenOf("idem"));

        Assert.Single(harness.Runtime.Sessions.Players, p => !p.IsLocal && p.IsSpawned);
        Assert.Equal(AccountOf("idem"), harness.Accounts.OwnerOf("Idem"));
    }
}
