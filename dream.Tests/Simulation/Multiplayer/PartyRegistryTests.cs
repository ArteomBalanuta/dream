using Dream.Simulation.Multiplayer;
using Xunit;

namespace Dream.Tests.Simulation.Multiplayer;

// M5: the pure party registry. Names go through CharacterNames.Comparer, the first member leads and the lead
// passes on in join order, a party of one dissolves, an invite lapses after 60 s.
public sealed class PartyRegistryTests
{
    private static PartyRegistry Pair(out ulong tick)
    {
        tick = 100;
        PartyRegistry registry = new();
        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "bob", tick));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("bob", true, tick, out _));
        return registry;
    }

    [Fact]
    public void InviteAccept_MakesAPartyOfTwo_WithTheInviterAsLeader()
    {
        PartyRegistry registry = Pair(out _);

        Assert.True(registry.TryGetMembers("alice", out IReadOnlyList<string> members));
        Assert.Equal(["alice", "bob"], members);
        Assert.True(registry.IsLeader("alice"));
        Assert.False(registry.IsLeader("bob"));
        Assert.True(registry.AreInSameParty("alice", "bob"));
        Assert.False(registry.AreInSameParty("alice", "carol"));
        Assert.Equal(1, registry.PartyCount);
        Assert.Equal(0, registry.InviteCount);
    }

    [Fact]
    public void Names_AreCaseFolded_LikeCharacterNames()
    {
        PartyRegistry registry = new();
        Assert.Equal(PartyOutcome.Ok, registry.Invite("Alice", "BOB", 1));
        Assert.Equal("Alice", registry.PendingInviteFrom("bob"));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("bob", true, 2, out _));
        Assert.True(registry.AreInSameParty("ALICE", "bob"));
    }

    [Fact]
    public void ASelfInvite_AnEmptyName_AndALongName_AreRefused()
    {
        PartyRegistry registry = new();

        Assert.Equal(PartyOutcome.SelfInvite, registry.Invite("alice", "Alice", 1));
        Assert.Equal(PartyOutcome.InvalidName, registry.Invite("alice", "  ", 1));
        Assert.Equal(PartyOutcome.InvalidName, registry.Invite("alice", null!, 1));
        Assert.Equal(PartyOutcome.InvalidName, registry.Invite("alice", new string('x', PartyRules.MaxTargetNameChars + 1), 1));
        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", new string('x', PartyRules.MaxTargetNameChars), 1));
    }

    [Fact]
    public void OnlyTheLeaderInvites_AndTheInviteeMustBeFree()
    {
        PartyRegistry registry = Pair(out ulong tick);

        Assert.Equal(PartyOutcome.NotLeader, registry.Invite("bob", "carol", tick));
        Assert.Equal(PartyOutcome.TargetInParty, registry.Invite("alice", "bob", tick));
        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "carol", tick));
        // One pending invite per invitee: a second inviter is refused, the same inviter may refresh.
        Assert.Equal(PartyOutcome.AlreadyInvited, registry.Invite("dave", "carol", tick));
        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "carol", tick));
    }

    [Fact]
    public void AFullParty_RefusesMoreInvites_AndAJoinThatRaced()
    {
        PartyRegistry registry = new();
        Assert.Equal(PartyOutcome.Ok, registry.Invite("m0", "m1", 1));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("m1", true, 1, out _));
        for (int i = 2; i < PartyRules.MaxMembers; i++)
        {
            Assert.Equal(PartyOutcome.Ok, registry.Invite("m0", "m" + i, 1));
            Assert.Equal(PartyOutcome.Ok, registry.Respond("m" + i, true, 1, out _));
        }

        registry.TryGetMembers("m0", out IReadOnlyList<string> members);
        Assert.Equal(PartyRules.MaxMembers, members.Count);
        Assert.Equal(PartyOutcome.PartyFull, registry.Invite("m0", "extra", 1));

        // An invite that was pending before the party filled up is refused on accept.
        PartyRegistry racing = new();
        Assert.Equal(PartyOutcome.Ok, racing.Invite("m0", "late", 1));
        Assert.Equal(PartyOutcome.Ok, racing.Invite("m0", "m1", 1));
        Assert.Equal(PartyOutcome.Ok, racing.Respond("m1", true, 1, out _));
        for (int i = 2; i < PartyRules.MaxMembers; i++)
        {
            Assert.Equal(PartyOutcome.Ok, racing.Invite("m0", "m" + i, 1));
            Assert.Equal(PartyOutcome.Ok, racing.Respond("m" + i, true, 1, out _));
        }

        Assert.Equal(PartyOutcome.PartyFull, racing.Respond("late", true, 1, out _));
        Assert.False(racing.IsInParty("late"));
        Assert.Null(racing.PendingInviteFrom("late"));
    }

    [Fact]
    public void ADeclineOrAnExpiredInvite_JoinsNothing_AndAnAnswerWithoutAnInviteIsRefused()
    {
        PartyRegistry registry = new();
        Assert.Equal(PartyOutcome.NoInvite, registry.Respond("bob", true, 1, out _));

        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "bob", 1));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("bob", false, 2, out string? inviter));
        Assert.Equal("alice", inviter);
        Assert.False(registry.IsInParty("alice"));
        Assert.Equal(0, registry.InviteCount);

        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "bob", 10));
        ulong late = 10 + (ulong)PartyRules.InviteTimeoutTicks + 1;
        Assert.Equal(PartyOutcome.InviteExpired, registry.Respond("bob", true, late, out _));
        Assert.False(registry.IsInParty("bob"));
    }

    [Fact]
    public void Expire_DropsLapsedInvites_AndMarksTheInvitee()
    {
        PartyRegistry registry = new();
        registry.Invite("alice", "bob", 10);
        List<string> changed = [];
        registry.DrainChanged(changed);
        changed.Clear();

        registry.Expire(10 + (ulong)PartyRules.InviteTimeoutTicks);
        Assert.Equal(1, registry.InviteCount);

        registry.Expire(10 + (ulong)PartyRules.InviteTimeoutTicks + 1);
        Assert.Equal(0, registry.InviteCount);
        registry.DrainChanged(changed);
        Assert.Equal(["bob"], changed);
    }

    [Fact]
    public void TheLead_PassesOnInJoinOrder_AndAPartyOfOneDissolves()
    {
        PartyRegistry registry = new();
        registry.Invite("a", "b", 1);
        registry.Respond("b", true, 1, out _);
        registry.Invite("a", "c", 1);
        registry.Respond("c", true, 1, out _);

        Assert.Equal(PartyOutcome.Ok, registry.Leave("a"));
        Assert.True(registry.TryGetMembers("b", out IReadOnlyList<string> members));
        Assert.Equal(["b", "c"], members);
        Assert.True(registry.IsLeader("b"));
        Assert.False(registry.IsInParty("a"));

        Assert.Equal(PartyOutcome.Ok, registry.Leave("c"));
        Assert.False(registry.IsInParty("b"));
        Assert.False(registry.IsInParty("c"));
        Assert.Equal(0, registry.PartyCount);
        Assert.Equal(PartyOutcome.NotInParty, registry.Leave("b"));
    }

    [Fact]
    public void Remove_LeavesTheParty_AndDropsInvitesToAndFromTheCharacter()
    {
        PartyRegistry registry = Pair(out ulong tick);
        registry.Invite("alice", "carol", tick);
        registry.Invite("dave", "erin", tick);

        registry.Remove("alice");

        Assert.False(registry.IsInParty("alice"));
        Assert.False(registry.IsInParty("bob"));
        Assert.Null(registry.PendingInviteFrom("carol"));
        Assert.Equal("dave", registry.PendingInviteFrom("erin"));
        registry.Remove("erin");
        Assert.Null(registry.PendingInviteFrom("erin"));
    }

    [Fact]
    public void EveryChange_RecordsTheKeysItTouched_Once()
    {
        PartyRegistry registry = new();
        registry.Invite("alice", "bob", 1);
        List<string> changed = [];
        registry.DrainChanged(changed);
        Assert.Equal(["bob"], changed);

        registry.Respond("bob", true, 1, out _);
        changed.Clear();
        registry.DrainChanged(changed);
        Assert.Equal(["alice", "bob"], changed.Order(StringComparer.Ordinal));
        changed.Clear();
        registry.DrainChanged(changed);
        Assert.Empty(changed);
    }
}
