using Dream.Simulation.Characters;
using Dream.Tests.Support;
using Xunit;

namespace Dream.Tests.Simulation.Characters;

// Wave G C0: every row of the CharacterAccessRules.Decide table.
public sealed class CharacterAccessRulesTests
{
    private static readonly AccountKey Me = TestAccountTokens.KeyFor("me");
    private static readonly AccountKey Other = TestAccountTokens.KeyFor("other");

    private static CharacterLookup Existing(AccountKey? owner, int owned, string key = "Bob", string display = "Bob") =>
        new(new CharacterRecordRef(new CharacterKey(key), display, owner), KeyReserved: false, owned);

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    public void Existing_OwnedByThisAccount_IsOwned_AtAnyCount(int owned)
    {
        CharacterAccessDecision decision = CharacterAccessRules.Decide("bob", Me, Existing(Me, owned, "Bob_key", "Bob"));

        Assert.Equal(CharacterAccessOutcome.Owned, decision.Outcome);
        Assert.Equal(new CharacterKey("Bob_key"), decision.Key);
        Assert.Equal("Bob", decision.DisplayName);
        Assert.Equal(Me, decision.Account);
        Assert.Null(decision.RejectionReason);
        Assert.Equal(new JoinHolderIdentity(new CharacterKey("Bob_key"), Me), decision.Holder);
        Assert.False(decision.IsRejected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Existing_OwnedByAnotherAccount_IsRejected_AtAnyCount(int owned)
    {
        CharacterAccessDecision decision = CharacterAccessRules.Decide("Bob", Me, Existing(Other, owned));

        Assert.True(decision.IsRejected);
        Assert.Equal(JoinRejectionReasons.CharacterOwned, decision.RejectionReason);
        Assert.Null(decision.Holder);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Existing_Unowned_UnderTheLimit_IsClaimed(int owned)
    {
        CharacterAccessDecision decision = CharacterAccessRules.Decide("Bob", Me, Existing(null, owned));

        Assert.Equal(CharacterAccessOutcome.ClaimUnowned, decision.Outcome);
        Assert.Equal(new CharacterKey("Bob"), decision.Key);
        Assert.Equal(Me, decision.Account);
        Assert.Equal(new JoinHolderIdentity(new CharacterKey("Bob"), Me), decision.Holder);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void Existing_Unowned_AtTheLimit_IsRejected(int owned)
    {
        CharacterAccessDecision decision = CharacterAccessRules.Decide("Bob", Me, Existing(null, owned));

        Assert.True(decision.IsRejected);
        Assert.Equal(JoinRejectionReasons.CharacterLimit, decision.RejectionReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void NoRow_KeyReserved_IsNameTaken_AtAnyCount(int owned)
    {
        CharacterAccessDecision decision = CharacterAccessRules.Decide("Bob", Me, new CharacterLookup(null, KeyReserved: true, owned));

        Assert.True(decision.IsRejected);
        Assert.Equal(JoinRejectionReasons.NameTaken, decision.RejectionReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\u0001")]
    [InlineData("123456789012345678901234567890123")]
    public void NoRow_InvalidName_IsRejectedAsInvalid_BeforeTheLimitIsChecked(string name)
    {
        Assert.Equal(
            JoinRejectionReasons.NameInvalid,
            CharacterAccessRules.Decide(name, Me, new CharacterLookup(null, false, 0)).RejectionReason);
        Assert.Equal(
            JoinRejectionReasons.NameInvalid,
            CharacterAccessRules.Decide(name, Me, new CharacterLookup(null, false, 5)).RejectionReason);
    }

    [Fact]
    public void NoRow_ValidName_AtTheLimit_IsRejected()
    {
        CharacterAccessDecision decision = CharacterAccessRules.Decide("Newbie", Me, new CharacterLookup(null, false, 5));

        Assert.True(decision.IsRejected);
        Assert.Equal(JoinRejectionReasons.CharacterLimit, decision.RejectionReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void NoRow_ValidName_UnderTheLimit_CreatesTheCharacter(int owned)
    {
        CharacterAccessDecision decision = CharacterAccessRules.Decide("Newbie", Me, new CharacterLookup(null, false, owned));

        Assert.Equal(CharacterAccessOutcome.CreateNew, decision.Outcome);
        Assert.Equal(new CharacterKey("Newbie"), decision.Key);
        Assert.Equal("Newbie", decision.DisplayName);
        Assert.Equal(Me, decision.Account);
        Assert.Equal(new JoinHolderIdentity(new CharacterKey("Newbie"), Me), decision.Holder);
    }

    [Fact]
    public void LegacyNames_AnyLength_StayValid_BecauseTheNameRuleOnlyAppliesToNewOnes()
    {
        string longName = new('x', 80);

        CharacterAccessDecision decision = CharacterAccessRules.Decide(longName, Me, Existing(Me, 1, longName, longName));

        Assert.Equal(CharacterAccessOutcome.Owned, decision.Outcome);
    }

    [Fact]
    public void Unchecked_HasNoHolder_AndIsNotRejected()
    {
        CharacterAccessDecision decision = CharacterAccessDecision.Unchecked("Bob");

        Assert.Equal(CharacterAccessOutcome.Unchecked, decision.Outcome);
        Assert.Equal("Bob", decision.DisplayName);
        Assert.Null(decision.Holder);
        Assert.False(decision.IsRejected);
        Assert.True(decision.Key.IsNone);
    }

    [Fact]
    public void InMemoryRepository_FollowsTheLookupContract()
    {
        InMemoryAccountRepository repository = new();
        repository.AddCharacter("Bob", "Bob", Me);
        repository.AddCharacter("Old", "Old", null);

        CharacterLookup mine = repository.Lookup("BOB", Me);
        Assert.Equal(new CharacterKey("Bob"), mine.Existing!.Value.Key);
        Assert.Equal(1, mine.OwnedByAccount);
        Assert.Equal(0, repository.Lookup("BOB", null).OwnedByAccount);
        Assert.True(repository.Lookup("bob", Other).Existing!.Value.Owner!.Value.Equals(Me));
        Assert.True(repository.RecordJoin(Other, new CharacterKey("Old"), claim: true, DateTime.UtcNow));
        Assert.False(repository.RecordJoin(Me, new CharacterKey("Old"), claim: true, DateTime.UtcNow));
        Assert.Equal(Other, repository.OwnerOf("Old"));
    }
}
