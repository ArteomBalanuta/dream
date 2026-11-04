using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Tests.Server;

// F5: the two command coalescers have to agree about SprintHeld. The client's
// ClientOutboundCommandQueue.Merge takes the NEWER frame's value, because sprint is held state and
// not an edge; LatestPlayerCommandInbox.Merge ORed it instead, copying JumpPressed's treatment. A
// coalesced pair in which the key was RELEASED then simulated one extra sprint tick server-side -
// small (about 0.027 m, inside the smooth band) but a divergence from what the client predicted,
// and two coalescers disagreeing about one field is the kind of thing that grows.
//
// This lives beside the server tests rather than in
// dream.Tests/Multiplayer/Sessions/LatestPlayerCommandInboxTests.cs because that file was owned by
// a parallel workstream in this wave; it belongs next to it and can be folded in later.
public class LatestInboxSprintCoalescingTests
{
    private static readonly PlayerSessionId Session = new("session-sprint");

    private static PlayerCommandFrame Frame(ulong sequence, bool sprintHeld, bool jumpPressed = false) =>
        new(
            Session,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0f, 0f, JumpPressed: jumpPressed, SprintHeld: sprintHeld),
            PlayerLookIntent.None,
            PrimaryUsePressed: false);

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void Coalescing_TakesTheNewerSprint_NotTheOrOfThePair(bool older, bool newer, bool expected)
    {
        LatestPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(1, older));
        inbox.Enqueue(Frame(2, newer));

        Assert.True(inbox.TryDrain(Session, out PlayerCommandFrame drained));

        Assert.Equal(2ul, drained.Sequence);
        Assert.Equal(expected, drained.MoveIntent.SprintHeld);

        // And the client's coalescer agrees, field for field, which is the actual contract.
        PlayerCommandFrame clientMerged = ClientOutboundCommandQueue.Merge(Frame(1, older), Frame(2, newer));
        Assert.Equal(clientMerged.MoveIntent.SprintHeld, drained.MoveIntent.SprintHeld);
    }

    // The one-shot presses are still ORed: a jump pressed in the dropped frame must not be lost.
    // That is what made someone OR the sprint in the first place, and it is correct for a jump.
    [Fact]
    public void Coalescing_StillKeepsAOneShotJumpFromTheDroppedFrame()
    {
        LatestPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(1, sprintHeld: true, jumpPressed: true));
        inbox.Enqueue(Frame(2, sprintHeld: false, jumpPressed: false));

        Assert.True(inbox.TryDrain(Session, out PlayerCommandFrame drained));

        Assert.True(drained.MoveIntent.JumpPressed);
        Assert.False(drained.MoveIntent.SprintHeld);
    }
}
