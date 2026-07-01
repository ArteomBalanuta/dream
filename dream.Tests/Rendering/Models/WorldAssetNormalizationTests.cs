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
}
