using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Core;

namespace Dream.Tests.Rendering.Core;

public class ReusableMatrixBufferTests
{
    [Fact]
    public void Rent_Reuses_Buffer_When_Capacity_Is_Sufficient()
    {
        ReusableMatrixBuffer buffer = new();

        Matrix4[] first = buffer.Rent(128);
        Matrix4[] second = buffer.Rent(64);

        Assert.Same(first, second);
        Assert.True(buffer.Capacity >= 128);
    }

    [Fact]
    public void Rent_Grows_Buffer_When_Capacity_Is_Too_Small()
    {
        ReusableMatrixBuffer buffer = new();

        Matrix4[] first = buffer.Rent(8);
        Matrix4[] second = buffer.Rent(32);

        Assert.NotSame(first, second);
        Assert.True(buffer.Capacity >= 32);
    }
}
