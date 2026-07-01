using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Tests.Multiplayer.Sessions;

public class PlayerActionQueueTests
{
    [Fact]
    public void Drain_ReturnsQueuedActions_InReceiveOrder()
    {
        PlayerActionQueue queue = new();
        PlayerSessionId sessionA = new("session-a");
        PlayerSessionId sessionB = new("session-b");

        queue.Enqueue(new PlayerActionMessage(
            sessionB,
            2,
            new CombatUseIntent(OpenTK.Mathematics.Vector3.Zero, -OpenTK.Mathematics.Vector3.UnitZ)));
        queue.Enqueue(new PlayerActionMessage(
            sessionA,
            1,
            new CombatUseIntent(OpenTK.Mathematics.Vector3.Zero, -OpenTK.Mathematics.Vector3.UnitZ)));

        IReadOnlyList<QueuedPlayerAction> drained = queue.Drain([sessionA, sessionB]);

        Assert.Collection(
            drained,
            first => Assert.Equal("session-b", first.SessionId.Value),
            second => Assert.Equal("session-a", second.SessionId.Value));
    }

    [Fact]
    public void Drain_LeavesUnjoinedActionsPending()
    {
        PlayerActionQueue queue = new();
        PlayerSessionId joined = new("joined");
        PlayerSessionId unjoined = new("unjoined");

        queue.Enqueue(new PlayerActionMessage(
            unjoined,
            1,
            new CombatUseIntent(OpenTK.Mathematics.Vector3.Zero, -OpenTK.Mathematics.Vector3.UnitZ)));
        queue.Enqueue(new PlayerActionMessage(
            joined,
            2,
            new CombatUseIntent(OpenTK.Mathematics.Vector3.Zero, -OpenTK.Mathematics.Vector3.UnitZ)));

        IReadOnlyList<QueuedPlayerAction> firstDrain = queue.Drain([joined]);
        IReadOnlyList<QueuedPlayerAction> secondDrain = queue.Drain([unjoined]);

        Assert.Single(firstDrain);
        Assert.Equal("joined", firstDrain[0].SessionId.Value);
        Assert.Single(secondDrain);
        Assert.Equal("unjoined", secondDrain[0].SessionId.Value);
    }
}
