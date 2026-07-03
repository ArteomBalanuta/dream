using Dream.Rendering.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

public class StaticWorldChunkKeyTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(15.99f, 0)]
    [InlineData(16f, 1)]
    [InlineData(-0.01f, -1)]
    [InlineData(-16f, -1)]
    [InlineData(-16.01f, -2)]
    public void FromWorldPositionUsesFloorDivision(float x, int expectedChunkX)
    {
        StaticWorldChunkKey key = StaticWorldChunkKey.FromWorldPosition(new Vector3(x, 0f, 0f));

        Assert.Equal(expectedChunkX, key.X);
        Assert.Equal(0, key.Y);
        Assert.Equal(0, key.Z);
    }

    [Fact]
    public void FromWorldPositionMapsEachAxisIndependently()
    {
        StaticWorldChunkKey key = StaticWorldChunkKey.FromWorldPosition(new Vector3(17f, 33f, -1f));

        Assert.Equal(new StaticWorldChunkKey(1, 2, -1), key);
    }
}
