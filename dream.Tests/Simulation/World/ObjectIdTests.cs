using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.Simulation.World;

public sealed class ObjectIdTests
{
    [Fact]
    public void None_IsDefault_AndIsNone()
    {
        Assert.True(ObjectId.None.IsNone);
        Assert.Equal(0L, ObjectId.None.Value);
        Assert.Equal(default, ObjectId.None);
        Assert.False(new ObjectId(1).IsNone);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Constructor_RejectsValuesBelowOne(long value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectId(value));
    }

    [Fact]
    public void Equality_ComparisonAndToString_FollowTheValue()
    {
        Assert.Equal(new ObjectId(7), new ObjectId(7));
        Assert.NotEqual(new ObjectId(7), new ObjectId(8));
        Assert.True(new ObjectId(3).CompareTo(new ObjectId(9)) < 0);
        Assert.True(new ObjectId(9).CompareTo(new ObjectId(3)) > 0);
        Assert.Equal(0, new ObjectId(5).CompareTo(new ObjectId(5)));
        Assert.Equal("1234567890123", new ObjectId(1_234_567_890_123L).ToString());
        Assert.Equal(
            [new ObjectId(1), new ObjectId(2), new ObjectId(10)],
            new[] { new ObjectId(10), new ObjectId(1), new ObjectId(2) }.Order().ToArray());
    }

    [Fact]
    public void Allocator_StartsAtOne_AndIssuesIncreasingIds()
    {
        ObjectIdAllocator allocator = new();
        Assert.Equal(1L, allocator.Next);
        Assert.Equal(new ObjectId(1), allocator.Take());
        Assert.Equal(new ObjectId(2), allocator.Take());
        Assert.Equal(3L, allocator.Next);
    }

    [Fact]
    public void Allocator_CustomStart_IsHonoured_AndInvalidStartRejected()
    {
        Assert.Equal(new ObjectId(500), new ObjectIdAllocator(500).Take());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectIdAllocator(0));
    }

    [Fact]
    public void Allocator_Restore_RaisesButNeverLowers()
    {
        ObjectIdAllocator allocator = new();
        allocator.Restore(50);
        Assert.Equal(50L, allocator.Next);
        allocator.Restore(10);
        Assert.Equal(50L, allocator.Next);
        Assert.Equal(new ObjectId(50), allocator.Take());
    }

    [Fact]
    public void Allocator_Reserve_MovesPastTakenIds_AndIgnoresNone()
    {
        ObjectIdAllocator allocator = new();
        allocator.Reserve(new ObjectId(9));
        Assert.Equal(10L, allocator.Next);
        allocator.Reserve(new ObjectId(4));
        Assert.Equal(10L, allocator.Next);
        allocator.Reserve(ObjectId.None);
        Assert.Equal(10L, allocator.Next);
        Assert.Equal(new ObjectId(10), allocator.Take());
    }

    [Fact]
    public void Allocator_AtLongMaxValue_ThrowsInsteadOfWrapping()
    {
        ObjectIdAllocator allocator = new(long.MaxValue);
        Assert.Throws<InvalidOperationException>(() => allocator.Take());
        Assert.Equal(long.MaxValue, allocator.Next);

        ObjectIdAllocator reserved = new();
        Assert.Throws<InvalidOperationException>(() => reserved.Reserve(new ObjectId(long.MaxValue)));
    }

    [Fact]
    public void Allocator_LastIdBeforeTheLimit_IsIssuedOnce()
    {
        ObjectIdAllocator allocator = new(long.MaxValue - 1);
        Assert.Equal(new ObjectId(long.MaxValue - 1), allocator.Take());
        Assert.Throws<InvalidOperationException>(() => allocator.Take());
    }

    [Fact]
    public void IdentityMap_BindsBothWays()
    {
        ObjectIdentityMap map = new();
        map.Bind(new ObjectId(100), 1000);
        map.Bind(new ObjectId(101), 2000);

        Assert.Equal(2, map.Count);
        Assert.True(map.TryGetObjectId(1000, out ObjectId cube));
        Assert.Equal(new ObjectId(100), cube);
        Assert.True(map.TryGetActorId(new ObjectId(101), out int enemy));
        Assert.Equal(2000, enemy);
        Assert.False(map.TryGetObjectId(1234, out ObjectId missing));
        Assert.True(missing.IsNone);
        Assert.False(map.TryGetActorId(new ObjectId(999), out int missingActor));
        Assert.Equal(0, missingActor);
    }

    [Fact]
    public void IdentityMap_Rebind_DropsTheEarlierBindingOfEitherSide()
    {
        ObjectIdentityMap map = new();
        map.Bind(new ObjectId(100), 1000);

        map.Bind(new ObjectId(200), 1000);
        Assert.Equal(1, map.Count);
        Assert.False(map.TryGetActorId(new ObjectId(100), out _));
        Assert.True(map.TryGetObjectId(1000, out ObjectId byActor));
        Assert.Equal(new ObjectId(200), byActor);

        map.Bind(new ObjectId(200), 1001);
        Assert.Equal(1, map.Count);
        Assert.False(map.TryGetObjectId(1000, out _));
        Assert.True(map.TryGetActorId(new ObjectId(200), out int byObject));
        Assert.Equal(1001, byObject);
    }

    [Fact]
    public void IdentityMap_BindingNone_Throws()
    {
        ObjectIdentityMap map = new();
        Assert.Throws<ArgumentException>(() => map.Bind(ObjectId.None, 1000));
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void IdentityMap_Remove_ByEitherSide_ClearsBoth()
    {
        ObjectIdentityMap map = new();
        map.Bind(new ObjectId(100), 1000);
        map.Bind(new ObjectId(101), 1001);

        Assert.True(map.RemoveByActorId(1000, out ObjectId removed));
        Assert.Equal(new ObjectId(100), removed);
        Assert.False(map.TryGetActorId(new ObjectId(100), out _));
        Assert.False(map.RemoveByActorId(1000, out ObjectId again));
        Assert.True(again.IsNone);

        Assert.True(map.RemoveByObjectId(new ObjectId(101)));
        Assert.False(map.TryGetObjectId(1001, out _));
        Assert.False(map.RemoveByObjectId(new ObjectId(101)));
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void IdentityMap_Clear_EmptiesBothDirections()
    {
        ObjectIdentityMap map = new();
        map.Bind(new ObjectId(100), 1000);
        map.Bind(new ObjectId(101), 2000);

        map.Clear();

        Assert.Equal(0, map.Count);
        Assert.False(map.TryGetObjectId(1000, out _));
        Assert.False(map.TryGetActorId(new ObjectId(101), out _));
    }
}
