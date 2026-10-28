using System.Numerics;
using SharpGLTF.Schema2;
using Xunit;
using Xunit.Abstractions;
using Dream.Rendering.Models;

namespace Dream.Tests.Integration;

// MEM-1 replaced SharpGLTF's per-call samplers with AnimatedModelPose's own keyframe arrays and
// hierarchy walk. These tests pin the pose to SharpGLTF's own evaluation for every clip of the
// shipped wolf and player models: at every keyframe, midway between keyframes, before the clip
// starts, after it ends, through looped and one-shot playback, across a mid-clip clip switch,
// and on rotation keys whose quaternions sit in opposite hemispheres (shortest-path slerp).
//
// The per-keyframe sweep lives in AnimatedModelPoseSharpGltfKeyframeParityIntegrationTests (sharded
// by clip over three classes), playback in AnimatedModelPoseSharpGltfPlaybackParityIntegrationTests,
// and the clip-switch and shortest-arc checks in
// AnimatedModelPoseSharpGltfClipSwitchParityIntegrationTests, so the heavy parts run in parallel.
// Those classes keep the shared helpers below and take the parsed models from a
// GltfModelRootFixture: the "reference" model is only evaluated through SharpGLTF's
// GetLocalTransform/GetWorldMatrix(animation, time), and the "posed" model is only read by
// AnimatedModelPose's constructor, so neither is ever written and both can be shared. The
// clip-switch test writes node transforms into its reference, so it loads its own.
public class AnimatedModelPoseSharpGltfParityIntegrationTests
{
    // Same relative tolerance as AnimatedModelAllocationTests: 1e-5 scaled by the matrix's
    // largest entry when that is above 1 (the wolf armature carries a scale of about 100).
    internal const float Tolerance = 1e-5f;

    private readonly ITestOutputHelper _output;

    public AnimatedModelPoseSharpGltfParityIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> ModelPaths() => new()
    {
        WolfAssetDefinition.ModelPath,
        PlayerModelAssetDefinition.ModelPath
    };

    [Fact]
    public void Constructed_Opposite_Hemisphere_Rotation_Keys_Take_The_Short_Way_Round()
    {
        // Identity, then 170 degrees about +Y written as its negated quaternion, so the stored
        // keys have dot = -cos(85 degrees) < 0. Halfway along the short arc is +85 degrees about
        // +Y; the long way round would be -95 degrees about +Y.
        ModelRoot model = ModelRoot.CreateModel();
        Scene scene = model.UseScene("parity");
        model.DefaultScene = scene;
        Node root = scene.CreateNode("root");
        Node child = root.CreateNode("child");
        child.LocalTransform = new SharpGLTF.Transforms.AffineTransform(Vector3.One, Quaternion.Identity, new Vector3(0f, 0f, 2f));
        Quaternion end = Quaternion.Negate(Quaternion.CreateFromAxisAngle(Vector3.UnitY, DegreesToRadians(170f)));
        Animation animation = model.CreateAnimation("spin");
        animation.CreateRotationChannel(root, new Dictionary<float, Quaternion>
        {
            [0f] = Quaternion.Identity,
            [1f] = end,
            [2f] = Quaternion.Identity
        });
        Assert.True(Quaternion.Dot(Quaternion.Identity, end) < 0f);

        AnimatedModelPose pose = new(model);
        pose.Apply(animation, 0.5f);

        Matrix4x4 expectedRoot = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, DegreesToRadians(85f)));
        ErrorTracker error = new();
        AssertClose(expectedRoot, pose.GetNodeWorldMatrix(root.LogicalIndex), error, "root at t=0.5");
        AssertClose(root.GetWorldMatrix(animation, 0.5f), pose.GetNodeWorldMatrix(root.LogicalIndex), error, "root vs SharpGLTF");
        AssertClose(child.GetWorldMatrix(animation, 0.5f), pose.GetNodeWorldMatrix(child.LogicalIndex), error, "child vs SharpGLTF");

        // The child sits at +2 Z under the root; rotated +85 degrees about Y it ends up at +X.
        Vector3 childPosition = pose.GetNodeWorldMatrix(child.LogicalIndex).Translation;
        Assert.True(childPosition.X > 1.9f, $"Child at {childPosition} went the long way round.");

        // Coming back from the negated key to identity is opposite-hemisphere too.
        pose.Apply(animation, 1.5f);
        AssertClose(expectedRoot, pose.GetNodeWorldMatrix(root.LogicalIndex), error, "root at t=1.5");
        AssertClose(root.GetWorldMatrix(animation, 1.5f), pose.GetNodeWorldMatrix(root.LogicalIndex), error, "root vs SharpGLTF t=1.5");

        // Exactly on the negated key both agree on the rotation (q and -q are the same rotation).
        pose.Apply(animation, 1f);
        AssertClose(root.GetWorldMatrix(animation, 1f), pose.GetNodeWorldMatrix(root.LogicalIndex), error, "root on key");
        _output.WriteLine($"constructed case max abs error {error.MaxAbsolute:E3}.");
    }

    internal static IEnumerable<float> CreateSampleTimes(Animation animation)
    {
        SortedSet<float> keyTimes = [];
        foreach (AnimationChannel channel in animation.Channels)
        {
            foreach (float key in GetKeyTimes(channel))
            {
                keyTimes.Add(key);
            }
        }

        float[] keys = keyTimes.ToArray();
        Assert.NotEmpty(keys);
        List<float> times = [];
        for (int i = 0; i < keys.Length; i++)
        {
            times.Add(keys[i]);
            if (i + 1 < keys.Length)
            {
                times.Add(keys[i] + (keys[i + 1] - keys[i]) * 0.5f);
                times.Add(keys[i] + (keys[i + 1] - keys[i]) * 0.1f);
            }
        }

        float duration = animation.Duration;
        times.AddRange([-1f, -0.0001f, -duration, duration, duration + 0.0001f, duration + 0.5f, duration * 2.5f, 1000f]);
        return times;
    }

    internal static IEnumerable<float> GetKeyTimes(AnimationChannel channel)
    {
        return channel.TargetNodePath switch
        {
            PropertyPath.rotation => channel.GetRotationSampler().GetLinearKeys().Select(key => key.Key),
            PropertyPath.translation => channel.GetTranslationSampler().GetLinearKeys().Select(key => key.Key),
            PropertyPath.scale => channel.GetScaleSampler().GetLinearKeys().Select(key => key.Key),
            _ => Array.Empty<float>()
        };
    }

    internal static void ApplyToGltfNodes(ModelRoot model, Animation animation, float time)
    {
        foreach (Node node in model.LogicalNodes)
        {
            if (animation.FindChannels(node).Any())
            {
                node.LocalTransform = node.GetLocalTransform(animation, time);
            }
        }
    }

    internal static void AssertPoseMatchesAnimation(
        ModelRoot reference,
        Animation animation,
        float time,
        AnimatedModelPose pose,
        ErrorTracker error,
        string context)
    {
        // SharpGLTF's world matrix of every node, evaluated once per sample: the joint check below
        // compares against the same values, so it reads them instead of walking each joint's
        // parents through SharpGLTF a second time.
        Matrix4x4[] referenceWorld = new Matrix4x4[reference.LogicalNodes.Count];
        for (int i = 0; i < reference.LogicalNodes.Count; i++)
        {
            Node node = reference.LogicalNodes[i];
            if (animation.FindChannels(node).Any())
            {
                error.Check(node.GetLocalTransform(animation, time).Matrix, pose.GetNodeLocalMatrix(i), $"{context} local node {i}");
            }

            referenceWorld[i] = node.GetWorldMatrix(animation, time);
            error.Check(referenceWorld[i], pose.GetNodeWorldMatrix(i), $"{context} world node {i}");
        }

        AssertJointMatricesMatch(reference, node => referenceWorld[node.LogicalIndex], pose, error, context);
    }

    internal static void AssertJointMatricesMatch(
        ModelRoot reference,
        Func<Node, Matrix4x4> referenceWorld,
        AnimatedModelPose pose,
        ErrorTracker error,
        string context)
    {
        if (reference.LogicalSkins.Count == 0)
        {
            return;
        }

        Skin skin = reference.LogicalSkins[0];
        Node meshNode = reference.LogicalNodes.First(node => node.Mesh != null);
        OpenTK.Mathematics.Matrix4 referenceMeshWorld = referenceWorld(meshNode).ToOpenTK();
        OpenTK.Mathematics.Matrix4 poseMeshWorld = pose.GetNodeWorldMatrix(meshNode.LogicalIndex).ToOpenTK();
        for (int i = 0; i < skin.JointsCount; i++)
        {
            (Node joint, Matrix4x4 inverseBind) = skin.GetJoint(i);
            OpenTK.Mathematics.Matrix4 expected = AnimatedModelTransformComposer.ComposeSkinJointMatrix(
                referenceMeshWorld,
                referenceWorld(joint).ToOpenTK(),
                inverseBind.ToOpenTK());
            OpenTK.Mathematics.Matrix4 actual = AnimatedModelTransformComposer.ComposeSkinJointMatrix(
                poseMeshWorld,
                pose.GetNodeWorldMatrix(joint.LogicalIndex).ToOpenTK(),
                inverseBind.ToOpenTK());
            error.Check(expected, actual, $"{context} joint {i}");
        }
    }

    internal static void AssertClose(Matrix4x4 expected, Matrix4x4 actual, ErrorTracker error, string context)
    {
        error.Check(expected, actual, context);
    }

    internal static Quaternion ReadRotation(Matrix4x4 local)
    {
        Assert.True(Matrix4x4.Decompose(local, out _, out Quaternion rotation, out _));
        return rotation;
    }

    // Independent of System.Numerics.Quaternion.Slerp, which both the pose and SharpGLTF use.
    internal static Quaternion ShortestArcSlerp(Quaternion from, Quaternion to, float amount)
    {
        double dot = (double)from.X * to.X + (double)from.Y * to.Y + (double)from.Z * to.Z + (double)from.W * to.W;
        double sign = 1d;
        if (dot < 0d)
        {
            dot = -dot;
            sign = -1d;
        }

        double fromWeight;
        double toWeight;
        if (dot > 0.999999d)
        {
            fromWeight = 1d - amount;
            toWeight = amount;
        }
        else
        {
            double theta = Math.Acos(Math.Min(1d, dot));
            double sinTheta = Math.Sin(theta);
            fromWeight = Math.Sin((1d - amount) * theta) / sinTheta;
            toWeight = Math.Sin(amount * theta) / sinTheta;
        }

        toWeight *= sign;
        return Quaternion.Normalize(new Quaternion(
            (float)(fromWeight * from.X + toWeight * to.X),
            (float)(fromWeight * from.Y + toWeight * to.Y),
            (float)(fromWeight * from.Z + toWeight * to.Z),
            (float)(fromWeight * from.W + toWeight * to.W)));
    }

    // The angle of the rotation that takes one orientation to the other; q and -q are equal.
    internal static float RotationAngleBetween(Quaternion left, Quaternion right)
    {
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(left), Quaternion.Normalize(right)));
        return 2f * MathF.Acos(MathF.Min(1f, dot));
    }

    internal static float DegreesToRadians(float degrees) => degrees * MathF.PI / 180f;

    internal sealed class ErrorTracker
    {
        public float MaxAbsolute { get; private set; }

        public float MaxRelative { get; private set; }

        public string Where { get; private set; } = "none";

        public void Check(Matrix4x4 expected, Matrix4x4 actual, string context)
        {
            Check(expected.ToOpenTK(), actual.ToOpenTK(), context);
        }

        public void Check(OpenTK.Mathematics.Matrix4 expected, OpenTK.Mathematics.Matrix4 actual, string context)
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
                    if (difference > MaxAbsolute)
                    {
                        MaxAbsolute = difference;
                    }

                    if (difference / magnitude > MaxRelative)
                    {
                        MaxRelative = difference / magnitude;
                        Where = context;
                    }

                    Assert.True(
                        difference <= tolerance,
                        $"{context} [{row},{column}]: expected {expected[row, column]} but was {actual[row, column]} (tolerance {tolerance}).");
                }
            }
        }
    }
}
