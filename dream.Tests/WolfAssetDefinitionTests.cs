using System.IO;
using Xunit;

public class WolfAssetDefinitionTests
{
    [Fact]
    public void ModelPath_Resolves_Under_Runtime_Enemy_Animation_Directory()
    {
        Assert.EndsWith(
            Path.Combine("animations", "enemies", "wolf.glb"),
            WolfAssetDefinition.ModelPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalization_Uses_Authored_And_Target_World_Height_Metadata()
    {
        Assert.Equal(3.191f, WolfAssetDefinition.Normalization.AuthoredHeight, 3);
        Assert.Equal(1.20f, WolfAssetDefinition.Normalization.TargetWorldHeight, 3);
        Assert.Equal(1.20f / 3.191f, WolfAssetDefinition.Normalization.ResolveUniformScale(), 3);
    }

    [Fact]
    public void VisualPresentation_Declares_Collision_Footprint()
    {
        Assert.Equal(0.45f, WolfAssetDefinition.CollisionRadius, 3);
        Assert.Equal(1.20f, WolfAssetDefinition.CollisionHeight, 3);
        Assert.Equal(0.95f, WolfAssetDefinition.PlayerSeparationDistance, 3);
    }
}
