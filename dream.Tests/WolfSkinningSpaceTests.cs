using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using Xunit;

public class WolfSkinningSpaceTests
{
    [Fact]
    public void Wolf_BindPose_Skinning_Stays_Near_Mesh_Local_Positions()
    {
        ModelRoot model = ModelRoot.Load(WolfAssetDefinition.ModelPath);
        Skin skin = Assert.Single(model.LogicalSkins);
        Node meshNode = Assert.Single(model.LogicalNodes, node => node.Mesh != null);
        Matrix4 meshWorldMatrix = meshNode.WorldMatrix.ToOpenTK();
        MeshPrimitive primitive = meshNode.Mesh!.Primitives[0];
        IReadOnlyList<System.Numerics.Vector3> positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
        IReadOnlyList<System.Numerics.Vector4> joints = primitive.GetVertexAccessor("JOINTS_0").AsVector4Array();
        IReadOnlyList<System.Numerics.Vector4> weights = primitive.GetVertexAccessor("WEIGHTS_0").AsVector4Array();

        float maxDistance = 0f;
        for (int i = 0; i < Math.Min(positions.Count, 500); i++)
        {
            Vector4 position = new(positions[i].X, positions[i].Y, positions[i].Z, 1f);
            Matrix4 skinMatrix = BuildWeightedSkinMatrix(skin, meshWorldMatrix, joints[i], weights[i]);
            Vector4 skinned = Vector4.TransformRow(position, skinMatrix);
            maxDistance = Math.Max(maxDistance, (skinned.Xyz - position.Xyz).Length);
        }

        Assert.InRange(maxDistance, 0f, 0.01f);
    }

    private static Matrix4 BuildWeightedSkinMatrix(
        Skin skin,
        Matrix4 meshWorldMatrix,
        System.Numerics.Vector4 joints,
        System.Numerics.Vector4 weights)
    {
        Matrix4 result = Matrix4.Zero;
        for (int i = 0; i < 4; i++)
        {
            float weight = GetComponent(weights, i);
            if (weight <= 0f)
            {
                continue;
            }

            int jointIndex = (int)MathF.Round(GetComponent(joints, i));
            (Node Joint, System.Numerics.Matrix4x4 InverseBindMatrix) joint = skin.GetJoint(jointIndex);
            result += AnimatedModelTransformComposer.ComposeSkinJointMatrix(
                meshWorldMatrix,
                joint.Joint.WorldMatrix.ToOpenTK(),
                joint.InverseBindMatrix.ToOpenTK()) * weight;
        }

        return result;
    }

    private static float GetComponent(System.Numerics.Vector4 vector, int index) => index switch
    {
        0 => vector.X,
        1 => vector.Y,
        2 => vector.Z,
        _ => vector.W
    };
}
