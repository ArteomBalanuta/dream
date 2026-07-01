using System.Reflection;
using Xunit;
using Dream.Rendering.Core;

namespace Dream.Tests.Rendering.Core;

public class RenderCubeDrawRangeTests
{
    [Fact]
    public void CreateCubeFaceDrawRanges_SplitsCubeVerticesIntoIndependentFaces()
    {
        MethodInfo method = typeof(Render).GetMethod(
            "CreateCubeFaceDrawRanges",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var ranges = Assert.IsType<ValueTuple<int, int>[]>(method.Invoke(null, [24]));

        Assert.Equal(
            [
                (0, 4),
                (4, 4),
                (8, 4),
                (12, 4),
                (16, 4),
                (20, 4)
            ],
            ranges);
    }
}
