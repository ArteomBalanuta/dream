using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Networking;

public class ClientActionSequenceTrackerTests
{
    [Fact]
    public void NextActionSequence_StartsAtOne()
    {
        ClientActionSequenceTracker tracker = new();

        Assert.Equal((ulong)1, tracker.NextActionSequence());
        Assert.Equal((ulong)2, tracker.NextActionSequence());
    }

    [Fact]
    public void ObserveCommandSequence_MovesHudActionsPastCommandDerivedPrimaryUseActions()
    {
        ClientActionSequenceTracker tracker = new();

        tracker.ObserveCommandSequence(48);

        Assert.Equal((ulong)49, tracker.NextActionSequence());
    }

    [Fact]
    public void ObserveActionResult_MovesHudActionsPastAcceptedServerAction()
    {
        ClientActionSequenceTracker tracker = new();

        tracker.ObserveActionResult(new AuthoritativeActionResult(
            "session-a",
            12,
            ServerTick: 64,
            Accepted: true,
            Payload: null));

        Assert.Equal((ulong)13, tracker.NextActionSequence());
    }

    [Fact]
    public void ObserveActionResult_DoesNotMoveBackwards()
    {
        ClientActionSequenceTracker tracker = new();
        tracker.ObserveCommandSequence(20);

        tracker.ObserveActionResult(new AuthoritativeActionResult(
            "session-a",
            5,
            ServerTick: 64,
            Accepted: true,
            Payload: null));

        Assert.Equal((ulong)21, tracker.NextActionSequence());
    }
}
