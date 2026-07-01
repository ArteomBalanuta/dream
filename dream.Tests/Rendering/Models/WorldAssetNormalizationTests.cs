using Xunit;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Models;


public class WorldAssetNormalizationTests
{
    [Fact]
    public void ResolveUniformScale_Returns_TargetHeight_Divided_By_AuthoredHeight()
    {
        WorldAssetNormalization normalization = new(3.191f, 1.20f);

        Assert.Equal(1.20f / 3.191f, normalization.ResolveUniformScale(), 3);
    }

    [Fact]
    public void ResolveScaledDimensions_Preserves_UniformScale_From_TargetHeight()
    {
        WorldAssetNormalization normalization = new(
            new WorldAssetDimensions(4f, 2f, 1f),
            new WorldAssetDimensions(2f, 1f, 1f));

        WorldAssetDimensions scaled = normalization.ResolveScaledDimensions();

        Assert.Equal(2f, scaled.Height, 3);
        Assert.Equal(1f, scaled.Width, 3);
        Assert.Equal(0.5f, scaled.Depth, 3);
    }
}
