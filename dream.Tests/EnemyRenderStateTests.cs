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

        Assert.Equal(expectedScale, new Vector3(state.ModelMatrix.M11, state.ModelMatrix.M12, state.ModelMatrix.M13).Length, 3);
        Assert.Equal(expectedScale, new Vector3(state.ModelMatrix.M21, state.ModelMatrix.M22, state.ModelMatrix.M23).Length, 3);
        Assert.Equal(expectedScale, new Vector3(state.ModelMatrix.M31, state.ModelMatrix.M32, state.ModelMatrix.M33).Length, 3);
    }

    [Theory]
    [InlineData(0f, 0f, -1f)]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 0f, 1f)]
    public void CreateWolf_Rotates_Authored_Forward_Toward_Movement(float x, float y, float z)
    {
        Vector3 desiredForward = new(x, y, z);
        EnemyRenderState state = EnemyRenderState.CreateWolf(
            Vector3.Zero,
            desiredForward,
            EnemyBehaviorState.Chasing,
            scale: 1f);

        Vector3 actualForward = Vector4.TransformRow(
            new Vector4(WolfAssetDefinition.AuthoredForwardAxis, 0f),
            state.ModelMatrix).Xyz.Normalized();

        Assert.Equal(desiredForward.X, actualForward.X, 3);
        Assert.Equal(desiredForward.Y, actualForward.Y, 3);
        Assert.Equal(desiredForward.Z, actualForward.Z, 3);
    }
}
