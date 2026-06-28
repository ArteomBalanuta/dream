using OpenTK.Mathematics;
using Xunit;

public class AnimatedModelTransformComposerTests
{
    [Fact]
    public void ComposeInstanceNodeMatrix_Preserves_Instance_Translation_When_Node_Has_Local_Scale()
    {
        Matrix4 instanceModelMatrix =
            Matrix4.CreateScale(0.85f) *
            Matrix4.CreateTranslation(new Vector3(5.5f, 0f, 2.5f));
        Matrix4 nodeWorldMatrix =
            Matrix4.CreateScale(100f) *
            Matrix4.CreateTranslation(new Vector3(0f, 0f, 0.061579935f));

        Matrix4 combined = AnimatedModelTransformComposer.ComposeInstanceNodeMatrix(instanceModelMatrix, nodeWorldMatrix);

        Assert.InRange(combined.M41, 5.49f, 5.51f);
        Assert.InRange(combined.M42, -0.01f, 0.01f);
        Assert.InRange(combined.M43, 2.54f, 2.57f);
    }

    [Fact]
    public void ComposeSkinJointMatrix_Cancels_Mesh_World_Transform_In_Bind_Pose()
    {
        Matrix4 meshWorldMatrix = Matrix4.CreateScale(100f);
        Matrix4 jointWorldMatrix = Matrix4.CreateScale(100f);
        Matrix4 inverseBindMatrix = Matrix4.Identity;

        Matrix4 skinMatrix = AnimatedModelTransformComposer.ComposeSkinJointMatrix(
            meshWorldMatrix,
            jointWorldMatrix,
            inverseBindMatrix);

        Assert.Equal(1f, skinMatrix.M11, 3);
        Assert.Equal(1f, skinMatrix.M22, 3);
        Assert.Equal(1f, skinMatrix.M33, 3);
    }
}
