using OpenTK.Mathematics;
using Xunit;

public class EnemyRenderStateTests
{
    [Fact]
    public void CreateWolf_Builds_Model_Matrix_From_Position_And_Forward()
    {
        EnemyRenderState state = EnemyRenderState.CreateWolf(
            new Vector3(4f, 1f, 5f),
            Vector3.UnitX,
            EnemyBehaviorState.Chasing,
            scale: 1.5f);

        Assert.Equal(WolfAssetDefinition.MoveClip, state.ClipName);
        Assert.Equal("enemy", state.ShaderName);
        Assert.Equal(4f, state.ModelMatrix.M41, 3);
        Assert.Equal(1f, state.ModelMatrix.M42, 3);
        Assert.Equal(5f, state.ModelMatrix.M43, 3);
    }

    [Fact]
    public void CreateWolf_Uses_Normalized_Default_Scale()
    {
        EnemyRenderState state = EnemyRenderState.CreateWolf(
            new Vector3(0f, 0f, 0f),
            -Vector3.UnitZ,
            EnemyBehaviorState.Idle);

        float expectedScale = WolfAssetDefinition.Normalization.ResolveUniformScale();

        Assert.Equal(expectedScale, state.ModelMatrix.M11, 3);
        Assert.Equal(expectedScale, state.ModelMatrix.M22, 3);
        Assert.Equal(expectedScale, state.ModelMatrix.M33, 3);
    }
}
