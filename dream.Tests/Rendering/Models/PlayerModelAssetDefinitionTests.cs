using SharpGLTF.Schema2;
using Xunit;
using Dream.Assets;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Models;


public class PlayerModelAssetDefinitionTests
{
    [Fact]
    public void PlayerModelAsset_ContainsExpectedClipsAndHeadNode()
    {
        Assert.True(File.Exists(PlayerModelAssetDefinition.ModelPath));
        Assert.True(File.Exists(AssetPaths.Animation("players/LICENSE-Kenney-Blocky-Characters.txt")));

        ModelRoot model = ModelRoot.Load(PlayerModelAssetDefinition.ModelPath);

        Assert.Contains(model.LogicalAnimations, animation => animation.Name == PlayerModelAssetDefinition.IdleClip);
        Assert.Contains(model.LogicalAnimations, animation => animation.Name == PlayerModelAssetDefinition.WalkClip);
        Assert.Contains(model.LogicalNodes, node => node.Name == PlayerModelAssetDefinition.HeadNodeName);
    }

    [Fact]
    public void PlayerModelAsset_Uses_WolfRelative_TargetHeight_And_Fits_PlayerEnvelope()
    {
        Assert.Equal(WolfAssetDefinition.Normalization.TargetWorldHeight * 2f, PlayerModelAssetDefinition.TargetWorldDimensions.Height, 3);
        Assert.True(PlayerModelAssetDefinition.TargetWorldDimensions.Width <= 1.0f);
        Assert.True(PlayerModelAssetDefinition.TargetWorldDimensions.Depth <= 1.0f);
        Assert.True(PlayerModelAssetDefinition.VisualScale > 0f);
    }

    [Fact]
    public void PlayerModelAsset_NormalizedDimensions_Match_TargetHeight_And_Stay_Compact()
    {
        WorldAssetDimensions scaled = PlayerModelAssetDefinition.Normalization.ResolveScaledDimensions();

        Assert.Equal(WolfAssetDefinition.Normalization.TargetWorldHeight * 2f, scaled.Height, 2);
        Assert.True(scaled.Width <= 1.0f, $"Player width should fit inside one cube. Actual: {scaled.Width}");
        Assert.True(scaled.Depth <= 1.0f, $"Player depth should fit inside one cube. Actual: {scaled.Depth}");
    }
}
