using Xunit;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Models;

public class GrassCubeAssetDefinitionTests
{
    [Fact]
    public void GrassCubeAsset_NormalizedDimensions_Match_UnitCubeTarget()
    {
        WorldAssetDimensions scaled = GrassCubeAssetDefinition.Normalization.ResolveScaledDimensions();

        Assert.Equal(1f, scaled.Height, 2);
        Assert.Equal(1f, scaled.Width, 2);
        Assert.Equal(1f, scaled.Depth, 2);
        Assert.True(GrassCubeAssetDefinition.VisualScale > 0f);
    }
}
