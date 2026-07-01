using SharpGLTF.Schema2;
using Xunit;

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
}
