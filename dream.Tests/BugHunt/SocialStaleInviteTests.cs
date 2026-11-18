using Dream.Simulation.Multiplayer;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-SOCIAL-4: a pending invite used to survive its invitee joining a party, so the invitee's panel
// offered an accept that could only ever answer AlreadyInParty while that invite held its one invite slot.
public sealed class SocialStaleInviteTests
{
    // BH-SOCIAL-4: C carries A's invite, then forms its own party with D. The invite it was carrying held
    // C's one invite slot while it could only ever answer AlreadyInParty.
    [Fact]
    public void JoiningAParty_DropsThePendingInviteTheNewMemberWasCarrying()
    {
        PartyRegistry registry = new();
        Assert.Equal(PartyOutcome.Ok, registry.Invite("a", "c", 1));
        Assert.Equal("a", registry.PendingInviteFrom("c"));

        // C invites D and D accepts: the new-party branch, where C is the inviter.
        Assert.Equal(PartyOutcome.Ok, registry.Invite("c", "d", 1));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("d", true, 2, out _));

        Assert.True(registry.AreInSameParty("c", "d"));
        Assert.Null(registry.PendingInviteFrom("c"));
        Assert.Equal(0, registry.InviteCount);
        // The slot is free again, so somebody else may now invite C once it leaves.
        Assert.Equal(PartyOutcome.Ok, registry.Leave("c"));
        Assert.Equal(PartyOutcome.Ok, registry.Invite("e", "c", 3));
        Assert.Equal("e", registry.PendingInviteFrom("c"));
    }

    // BH-SOCIAL-4: the dropped invite is reported as a change, so the invitee's panel loses the dead
    // accept affordance in the same push that shows it its new party.
    [Fact]
    public void TheDroppedInvite_IsRecordedAsAChangeForTheNewMember()
    {
        PartyRegistry registry = new();
        registry.Invite("a", "c", 1);
        registry.Invite("c", "d", 1);
        List<string> changed = [];
        registry.DrainChanged(changed);
        changed.Clear();

        registry.Respond("d", true, 2, out _);

        registry.DrainChanged(changed);
        Assert.Equal(["c", "d"], changed.Order(StringComparer.Ordinal));
        Assert.False(registry.HasChanges);
    }

    // BH-SOCIAL-4 control: an invite to somebody who did NOT join a party is left alone, so a normal
    // pending invite still waits out its 60 s and is still accepted.
    [Fact]
    public void AnUnrelatedPendingInvite_SurvivesSomeoneElseFormingAParty()
    {
        PartyRegistry registry = new();
        Assert.Equal(PartyOutcome.Ok, registry.Invite("a", "waiting", 1));
        Assert.Equal(PartyOutcome.Ok, registry.Invite("b", "joiner", 1));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("joiner", true, 2, out _));

        Assert.Equal("a", registry.PendingInviteFrom("waiting"));
        Assert.Equal(PartyOutcome.Ok, registry.Respond("waiting", true, 3, out _));
        Assert.True(registry.AreInSameParty("a", "waiting"));
        Assert.Equal(2, registry.PartyCount);
    }
}
