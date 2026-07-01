using Xunit;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Models;

public class WorldAssetBoundsTests
{
    [Fact]
    public void MeasureDimensions_Returns_Positive_Player_Bounds()
    {
        WorldAssetDimensions dimensions = WorldAssetBounds.MeasureDimensions(PlayerModelAssetDefinition.ModelPath);

        Assert.True(dimensions.HasPositiveVolume);
    }

    [Fact]
    public void MeasureDimensions_Returns_Positive_Wolf_Bounds()
    {
        WorldAssetDimensions dimensions = WorldAssetBounds.MeasureDimensions(WolfAssetDefinition.ModelPath);

        Assert.True(dimensions.HasPositiveVolume);
    }
}
