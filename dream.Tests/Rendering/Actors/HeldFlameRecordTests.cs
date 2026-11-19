using Dream.Rendering.Actors;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Actors;

// The remote player's recorded flame survives frames in which the player is not drawn, and only those.
public sealed class HeldFlameRecordTests
{
    private static readonly PlayerEquipmentVisuals Torch = new(MainHand: null, OffHand: "held.tool.torch");
    private static readonly Vector3 Point = new(0.26f, 1.14f, 0.57f);

    private static HeldFlameRecord Recorded()
    {
        HeldFlameRecord record = new();
        record.Record("session-a", in Torch, hasFlame: true, Point);
        return record;
    }

    [Fact]
    public void KeepsReturningThePoint_WhileNothingNewIsRecorded()
    {
        HeldFlameRecord record = Recorded();

        for (int frame = 0; frame < 5; frame++)
        {
            Assert.True(record.TryGet("session-a", in Torch, out Vector3 point));
            Assert.Equal(Point, point);
        }
    }

    [Fact]
    public void NeverRecorded_HasNoPoint()
    {
        Assert.False(new HeldFlameRecord().TryGet("session-a", in Torch, out Vector3 point));
        Assert.Equal(Vector3.Zero, point);
    }

    [Fact]
    public void ARecycledActorId_DoesNotInheritThePreviousOwnersPoint()
    {
        HeldFlameRecord record = Recorded();

        Assert.False(record.TryGet("session-b", in Torch, out Vector3 point));
        Assert.Equal(Vector3.Zero, point);
    }

    [Fact]
    public void AHeldItemChange_ExpiresThePoint()
    {
        HeldFlameRecord record = Recorded();
        PlayerEquipmentVisuals lantern = new(MainHand: null, OffHand: "held.tool.lantern");
        PlayerEquipmentVisuals mainTorch = new(MainHand: "held.tool.torch", OffHand: null);

        Assert.False(record.TryGet("session-a", in lantern, out _));
        Assert.False(record.TryGet("session-a", in mainTorch, out _));
        Assert.False(record.TryGet("session-a", PlayerEquipmentVisuals.Empty, out _));
    }

    [Fact]
    public void ADrawWithNoLitItem_ClearsThePoint()
    {
        HeldFlameRecord record = Recorded();

        record.Record("session-a", in Torch, hasFlame: false, Vector3.Zero);

        Assert.False(record.TryGet("session-a", in Torch, out _));
    }
}
