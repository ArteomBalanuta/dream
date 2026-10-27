using System.Linq;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using Xunit;
using Dream.Rendering.Models;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Models;


public class AnimatedModelAllocationTests
{
    private const float Tolerance = 1e-5f;
    private static readonly float[] SampleTimes = [0f, 0.1f, 0.37f, 0.5f, 1.25f, 2.9f];

    public static TheoryData<string> ModelPaths() => new()
    {
        WolfAssetDefinition.ModelPath,
        PlayerModelAssetDefinition.ModelPath
    };

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void RenderableNodes_Match_Iterator_Order(string modelPath)
    {
        ModelRoot model = ModelRoot.Load(modelPath);
        AnimatedModelPose pose = new(model);
        Node[] expected = SceneNodeTraversal.EnumerateRenderableNodes(model.DefaultScene.VisualChildren).ToArray();

        List<Node> collected = [];
        SceneNodeTraversal.CollectRenderableNodes(model.DefaultScene.VisualChildren, collected);

        Assert.NotEmpty(expected);
        Assert.Equal(expected, collected);
        Assert.Equal(expected.Length, pose.RenderableNodeCount);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Same(expected[i], pose.GetRenderableNode(i));
        }
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void Rest_Pose_Matches_Gltf_World_Matrices(string modelPath)
    {
        ModelRoot model = ModelRoot.Load(modelPath);
        AnimatedModelPose pose = new(model);

        for (int i = 0; i < model.LogicalNodes.Count; i++)
        {
            AssertClose(model.LogicalNodes[i].WorldMatrix, pose.GetNodeWorldMatrix(i));
        }
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void Cached_Samplers_Match_GetLocalTransform(string modelPath)
    {
        ModelRoot reference = ModelRoot.Load(modelPath);
        ModelRoot posed = ModelRoot.Load(modelPath);
        Assert.NotEmpty(reference.LogicalAnimations);

        for (int a = 0; a < reference.LogicalAnimations.Count; a++)
        {
            Animation referenceAnimation = reference.LogicalAnimations[a];
            AnimatedModelPose pose = new(posed);
            Animation posedAnimation = posed.LogicalAnimations[a];
            foreach (float time in SampleTimes)
            {
                pose.Apply(posedAnimation, time);
                foreach (Node node in reference.LogicalNodes)
                {
                    if (!referenceAnimation.FindChannels(node).Any())
                    {
                        continue;
                    }

                    AssertClose(
                        node.GetLocalTransform(referenceAnimation, time).Matrix,
                        pose.GetNodeLocalMatrix(node.LogicalIndex));
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void Pose_World_And_Joint_Matrices_Match_Gltf_Node_Updates_Across_Clip_Changes(string modelPath)
    {
        ModelRoot reference = ModelRoot.Load(modelPath);
        ModelRoot posed = ModelRoot.Load(modelPath);
        AnimatedModelPose pose = new(posed);

        // Switching clips keeps the channels the next clip does not animate, so the order matters.
        for (int a = 0; a < reference.LogicalAnimations.Count; a++)
        {
            Animation referenceAnimation = reference.LogicalAnimations[a];
            float time = SampleTimes[a % SampleTimes.Length];
            foreach (Node node in reference.LogicalNodes)
            {
                if (referenceAnimation.FindChannels(node).Any())
                {
                    node.LocalTransform = node.GetLocalTransform(referenceAnimation, time);
                }
            }

            pose.Apply(posed.LogicalAnimations[a], time);

            for (int i = 0; i < reference.LogicalNodes.Count; i++)
            {
                AssertClose(reference.LogicalNodes[i].WorldMatrix, pose.GetNodeWorldMatrix(i));
            }

            AssertJointMatricesMatch(reference, pose);
        }
    }

    [Fact]
    public void Joint_Uniform_Name_Table_Matches_Shader_Array_And_Allocates_Nothing()
    {
        for (int i = 0; i < 64; i++)
        {
            Assert.Equal($"jointMatrices[{i}]", AnimatedModel.GetJointMatrixUniformName(i));
        }

        int totalLength = 0;
        long bytes = AllocationAssert.AtMost(0, () =>
        {
            for (int i = 0; i < 64; i++)
            {
                totalLength += AnimatedModel.GetJointMatrixUniformName(i).Length;
            }
        });

        Assert.Equal(0, bytes);
        Assert.True(totalLength > 0);
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void Pose_Apply_Allocates_Nothing_After_Warmup(string modelPath)
    {
        ModelRoot model = ModelRoot.Load(modelPath);
        AnimatedModelPose pose = new(model);
        Animation animation = model.LogicalAnimations.First(a => pose.GetAnimatedNodeCount(a) > 0);
        float time = 0f;

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            time += 1f / 60f;
            pose.Apply(animation, time);
        });

        Assert.Equal(0, bytes);
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void Render_Matrix_Reads_Allocate_Nothing(string modelPath)
    {
        ModelRoot model = ModelRoot.Load(modelPath);
        AnimatedModelPose pose = new(model);
        float checksum = 0f;

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            for (int i = 0; i < pose.RenderableNodeCount; i++)
            {
                checksum += pose.GetRenderableNodeWorldMatrix(i).ToOpenTK().M44;
                checksum += pose.GetRenderableNode(i).Name?.Length ?? 0;
            }

            for (int i = 0; i < pose.NodeCount; i++)
            {
                checksum += pose.GetNodeWorldMatrix(i).ToOpenTK().M44;
            }
        });

        Assert.Equal(0, bytes);
        Assert.True(checksum > 0f);
    }

    private static void AssertJointMatricesMatch(ModelRoot reference, AnimatedModelPose pose)
    {
        if (reference.LogicalSkins.Count == 0)
        {
            return;
        }

        Skin skin = reference.LogicalSkins[0];
        Node meshNode = reference.LogicalNodes.First(node => node.Mesh != null);
        Matrix4 referenceMeshWorld = meshNode.WorldMatrix.ToOpenTK();
        Matrix4 poseMeshWorld = pose.GetNodeWorldMatrix(meshNode.LogicalIndex).ToOpenTK();
        for (int i = 0; i < skin.JointsCount; i++)
        {
            (Node joint, System.Numerics.Matrix4x4 inverseBind) = skin.GetJoint(i);
            Matrix4 expected = AnimatedModelTransformComposer.ComposeSkinJointMatrix(
                referenceMeshWorld,
                joint.WorldMatrix.ToOpenTK(),
                inverseBind.ToOpenTK());
            Matrix4 actual = AnimatedModelTransformComposer.ComposeSkinJointMatrix(
                poseMeshWorld,
                pose.GetNodeWorldMatrix(joint.LogicalIndex).ToOpenTK(),
                inverseBind.ToOpenTK());
            AssertClose(expected, actual);
        }
    }

    private static void AssertClose(System.Numerics.Matrix4x4 expected, System.Numerics.Matrix4x4 actual)
    {
        AssertClose(expected.ToOpenTK(), actual.ToOpenTK());
    }

    // 1e-5, scaled by the matrix's largest entry when that is above 1. The wolf armature's world
    // matrices carry a scale of about 100, and SharpGLTF's own Node.WorldMatrix already differs
    // from a plain float product of the local matrices by several 1e-6 at that magnitude.
    private static void AssertClose(Matrix4 expected, Matrix4 actual)
    {
        float magnitude = 1f;
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                magnitude = MathF.Max(magnitude, MathF.Abs(expected[row, column]));
            }
        }

        float tolerance = Tolerance * magnitude;
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                float difference = MathF.Abs(expected[row, column] - actual[row, column]);
                Assert.True(
                    difference <= tolerance,
                    $"[{row},{column}] expected {expected[row, column]} but was {actual[row, column]}.");
            }
        }
    }
}
