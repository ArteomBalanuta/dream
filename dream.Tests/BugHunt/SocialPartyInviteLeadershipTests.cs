using Dream.Simulation.Multiplayer;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-SOCIAL-1: a party invite is a deferred action, so the state that made it legitimate can change
// before it is redeemed. Leadership is now re-checked at the moment of effect, so an inviter who has since
// joined somebody else's party as a plain member cannot admit anyone to it.
public sealed class SocialPartyInviteLeadershipTests
{
    // BH-SOCIAL-1: the exact ordering from the packet. A, in no party, invites C; A then joins X's party as
    // a member; C accepts. Before the fix C was added to X's party, which X never invited it to and cannot
    // remove it from (there is no kick verb).
    [Fact]
    public void AnInviteFromSomeoneWhoSinceJoinedAnotherPartyAsAMember_IsRefusedOnAccept()
    {
        PartyRegistry registry = new();
        const ulong tick = 100;

        // 1: A is in no party and invites C.
        Assert.Equal(PartyOutcome.Ok, registry.Invite("a", "c", tick));

        // 2-3: X invites A, A accepts, so A is a member of X's party and not its leader.
        Assert.Equal(PartyOutcome.Ok, registry.Invite("x", "a", tick));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("a", true, tick, out _));
        Assert.True(registry.IsLeader("x"));
        Assert.False(registry.IsLeader("a"));

        // 4: C answers A's invite, still inside the 60 s window.
        Assert.Equal(PartyOutcome.NotLeader, registry.Respond("c", true, tick + 1, out string? inviter));

        Assert.Equal("a", inviter);
        Assert.False(registry.IsInParty("c"));
        Assert.True(registry.TryGetMembers("x", out IReadOnlyList<string> members));
        Assert.Equal(["x", "a"], members);
        // The invite is consumed either way, so C is free to be invited again.
        Assert.Null(registry.PendingInviteFrom("c"));
        Assert.Equal(1, registry.PartyCount);
    }

    // BH-SOCIAL-1 control: the ordinary paths are untouched. A real leader's pending invite is still
    // accepted into its party, and two strangers still form one.
    [Fact]
    public void ALeadersInvite_IsStillAccepted_AndTwoStrangersStillFormAParty()
    {
        PartyRegistry registry = new();

        // Two strangers: the inviter is in no party at accept time, which must stay legitimate.
        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "bob", 1));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("bob", true, 2, out _));
        Assert.True(registry.AreInSameParty("alice", "bob"));
        Assert.Equal(1, registry.PartyCount);

        // The leader of that party invites a third, who accepts after the party already exists.
        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "carol", 3));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("carol", true, 4, out _));
        Assert.True(registry.TryGetMembers("alice", out IReadOnlyList<string> members));
        Assert.Equal(["alice", "bob", "carol"], members);

        // An invite issued by a leader that is still the leader when the lead passes to it also holds: the
        // lead moves on in join order, so bob leads once alice leaves and bob's own invite then works.
        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "dave", 5));
        Assert.Equal(PartyOutcome.Ok, registry.Leave("alice"));
        Assert.True(registry.IsLeader("bob"));
        // Dave's invite came from a key that is no longer in the party at all: that is the new-party branch,
        // which is unchanged, so dave and alice pair up rather than joining bob's party.
        Assert.Equal(PartyOutcome.Ok, registry.Respond("dave", true, 6, out _));
        Assert.True(registry.AreInSameParty("alice", "dave"));
        Assert.False(registry.AreInSameParty("bob", "dave"));
    }

    // BH-SOCIAL-1: the fix is a leadership test, not an "inviter changed party" test, so an invite from a
    // leader whose party grew (but who still leads) is still accepted.
    [Fact]
    public void AnInviteFromALeaderWhosePartyGrewMeanwhile_IsStillAccepted()
    {
        PartyRegistry registry = new();
        Assert.Equal(PartyOutcome.Ok, registry.Invite("lead", "first", 1));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("first", true, 1, out _));

        // Two invites out at once from the same leader; both land.
        Assert.Equal(PartyOutcome.Ok, registry.Invite("lead", "second", 1));
        Assert.Equal(PartyOutcome.Ok, registry.Invite("lead", "third", 1));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("second", true, 2, out _));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("third", true, 3, out _));

        Assert.True(registry.TryGetMembers("lead", out IReadOnlyList<string> members));
        Assert.Equal(["lead", "first", "second", "third"], members);
    }}
