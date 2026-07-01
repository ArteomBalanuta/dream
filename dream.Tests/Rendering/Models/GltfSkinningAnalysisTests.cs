using Xunit;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Models;


public class GltfSkinningAnalysisTests
{
    [Fact]
    public void Analyze_WolfAsset_Detects_Skinned_Primitives()
    {
        GltfSkinningAnalysisResult result = GltfSkinningAnalysis.Analyze(WolfAssetDefinition.ModelPath);

        Assert.True(result.HasSkins);
        Assert.True(result.HasSkinnedPrimitives);
        Assert.Equal(49, result.JointCount);
    }
}
