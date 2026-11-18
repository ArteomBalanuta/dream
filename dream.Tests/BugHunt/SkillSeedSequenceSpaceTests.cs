using Dream.Client.Networking;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-SKILL-4, re-verified and REJECTED. The report was that a skill cast and a server-synthesized
// primary attack landing on one tick could draw the identical damage roll, because
// AuthoritativeCombatResolver.ResolveSeededAttack keys the roll on (simulation seed, tick, session,
// action sequence, target) and the synthesized primary carries its COMMAND frame's sequence, from a
// space that overlaps the client's action sequences numerically.
//
// The roll being a pure function of those five inputs is the resolver's deliberate, documented
// contract, pinned from both sides by dream.Tests/Simulation/Combat/SkillResolutionTests.cs:197-201
// (a bolt's damage equals that function) and :292-295 (a primary attack's does). The two sequence
// spaces are kept apart on the client instead, and that is what the test below pins: PlayScene's
// prediction tick calls ClientActionSequenceTracker.ObserveCommandSequence(tick) before anything in
// that frame can allocate an action sequence (dream/Client/Scenes/PlayScene.Update.cs:176-177), and a
// command frame's Sequence is the prediction tick itself (ClientCommandTickStream.cs:146,181). So
// every action sequence a client issues is strictly greater than the command sequence of the tick it
// was issued on, and the command that will one day carry that same number belongs to a strictly later
// client tick -- it cannot be the command the server is repeating on the tick the action arrives.
//
// So no change was made: there is no reachable defect here, and adding a per-intent discriminator to
// the seed would contradict the two tests above for no gain. Changing the seeding is also the one
// thing in this area that must not be done lightly -- lockstep peers compare checksums every tick.
public sealed class SkillSeedSequenceSpaceTests
{
    // The guard: an action sequence allocated on client tick T is always past T, so it can never equal
    // the command sequence of the frame being sent on that same tick.
    [Fact]
    public void AnActionSequence_IsAlwaysPastTheCommandSequenceOfItsOwnTick()
    {
        ClientActionSequenceTracker tracker = new();

        for (ulong tick = 1; tick <= 64; tick++)
        {
            tracker.ObserveCommandSequence(tick);
            ulong action = tracker.NextActionSequence();
            Assert.True(action > tick, $"action {action} is not past command {tick}");
        }
    }

    // And it stays past it however many actions one tick issues, so a burst cannot walk back into the
    // command space either.
    [Fact]
    public void SeveralActionsOnOneTick_AllStayPastThatTicksCommandSequence()
    {
        ClientActionSequenceTracker tracker = new();
        tracker.ObserveCommandSequence(1_000);

        for (int i = 0; i < 8; i++)
        {
            Assert.True(tracker.NextActionSequence() > 1_000UL);
        }
    }
}
