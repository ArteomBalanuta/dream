using System.Numerics;
using SharpGLTF.Schema2;
using Xunit;
using Xunit.Abstractions;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Models;

// The shipped wolf and player clips only use LINEAR channels with several keys, so the parity
// tests against them never reach AnimatedModelPose's STEP branch, its single-key channels or its
// CUBICSPLINE fallback to SharpGLTF's own curve sampler. These tests build small glTF models in
// code that use exactly those paths and compare the pose with SharpGLTF's GetLocalTransform and
// GetWorldMatrix(animation, time) on keys, between keys, and before and after the clip.
public class AnimatedModelPoseSamplerPathTests
{
    private const float Tolerance = 1e-5f;

    private static readonly float[] KeyTimes = [0.5f, 1.25f, 2f];

    private readonly ITestOutputHelper _output;

    public AnimatedModelPoseSamplerPathTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Step_Channels_Match_SharpGltf_On_Between_And_Outside_Keys()
    {
        ModelRoot model = CreateTwoNodeModel(out Node root, out Node child);
        Animation animation = model.CreateAnimation("step");
        animation.CreateRotationChannel(root, Keys(RotationValues()), linear: false);
        animation.CreateTranslationChannel(root, Keys(TranslationValues()), linear: false);
        animation.CreateScaleChannel(child, Keys(ScaleValues()), linear: false);
        AssertInterpolation(animation, root, AnimationInterpolationMode.STEP);

        float maxError = AssertPoseMatchesSharpGltf(model, animation, CreateSampleTimes(KeyTimes));

        // STEP really holds a key's value until the next key, rather than blending.
        AnimatedModelPose pose = new(model);
        pose.Apply(animation, 1.2f);
        Assert.Equal(TranslationValues()[0], pose.GetNodeLocalMatrix(root.LogicalIndex).Translation);
        pose.Apply(animation, 1.3f);
        Assert.Equal(TranslationValues()[1], pose.GetNodeLocalMatrix(root.LogicalIndex).Translation);

        _output.WriteLine($"STEP: max abs error {maxError:E3}.");
    }

    [Fact]
    public void Single_Key_Channels_Hold_Their_Value_Everywhere_Like_SharpGltf()
    {
        ModelRoot model = CreateTwoNodeModel(out Node root, out Node child);
        Animation animation = model.CreateAnimation("single");
        Quaternion singleRotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1f, 2f, 0.5f)), 0.8f);
        Vector3 singleTranslation = new(3f, -1f, 0.25f);
        Vector3 singleScale = new(1.5f, 0.75f, 2f);
        animation.CreateRotationChannel(root, new Dictionary<float, Quaternion> { [0.75f] = singleRotation });
        animation.CreateTranslationChannel(root, new Dictionary<float, Vector3> { [0.75f] = singleTranslation }, linear: false);
        animation.CreateScaleChannel(child, new Dictionary<float, Vector3> { [1.5f] = singleScale });
        // A multi-key channel on the child as well, so the clip has a real range around the single keys.
        animation.CreateTranslationChannel(child, Keys(TranslationValues()));

        float[] times = [.. CreateSampleTimes(KeyTimes), 0.75f, 0.7499f, 0.7501f, 1.5f, 1.4999f, 1.5001f];
        float maxError = AssertPoseMatchesSharpGltf(model, animation, times);

        AnimatedModelPose pose = new(model);
        foreach (float time in new[] { -3f, 0f, 0.75f, 1f, 100f })
        {
            pose.Apply(animation, time);
            Assert.True(Matrix4x4.Decompose(pose.GetNodeLocalMatrix(root.LogicalIndex), out _, out Quaternion rotation, out Vector3 translation));
            Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(rotation, singleRotation)) - 1f) < 1e-5f, $"t={time}: rotation {rotation}");
            Assert.True(Vector3.Distance(singleTranslation, translation) < 1e-5f, $"t={time}: translation {translation}");
        }

        _output.WriteLine($"single key: max abs error {maxError:E3}.");
    }

    [Fact]
    public void CubicSpline_Channels_Match_SharpGltf_On_Between_And_Outside_Keys()
    {
        ModelRoot model = CreateTwoNodeModel(out Node root, out Node child);
        Animation animation = model.CreateAnimation("cubic");
        Quaternion[] rotations = RotationValues();
        Vector3[] translations = TranslationValues();
        Vector3[] scales = ScaleValues();
        Dictionary<float, (Quaternion TangentIn, Quaternion Value, Quaternion TangentOut)> rotationKeys = [];
        Dictionary<float, (Vector3 TangentIn, Vector3 Value, Vector3 TangentOut)> translationKeys = [];
        Dictionary<float, (Vector3 TangentIn, Vector3 Value, Vector3 TangentOut)> scaleKeys = [];
        for (int i = 0; i < KeyTimes.Length; i++)
        {
            // Non-zero, asymmetric tangents, so the curve really is a Hermite spline.
            Quaternion rotationTangent = new(0.1f * (i + 1), -0.05f, 0.2f, 0.03f * i);
            rotationKeys[KeyTimes[i]] = (rotationTangent, rotations[i], Quaternion.Negate(rotationTangent));
            translationKeys[KeyTimes[i]] = (new Vector3(1f, -0.5f * i, 2f), translations[i], new Vector3(-0.75f, 0.3f, 1f + i));
            scaleKeys[KeyTimes[i]] = (new Vector3(0.2f, 0.1f, -0.1f), scales[i], new Vector3(-0.3f, 0.25f, 0.05f * i));
        }

        animation.CreateRotationChannel(root, rotationKeys);
        animation.CreateTranslationChannel(root, translationKeys);
        animation.CreateScaleChannel(child, scaleKeys);
        AssertInterpolation(animation, root, AnimationInterpolationMode.CUBICSPLINE);

        float maxError = AssertPoseMatchesSharpGltf(model, animation, CreateSampleTimes(KeyTimes));

        // Between keys the spline is not the straight line between the key values.
        AnimatedModelPose pose = new(model);
        float mid = (KeyTimes[0] + KeyTimes[1]) * 0.5f;
        pose.Apply(animation, mid);
        Vector3 linearMid = Vector3.Lerp(translations[0], translations[1], 0.5f);
        Assert.True(Vector3.Distance(linearMid, pose.GetNodeLocalMatrix(root.LogicalIndex).Translation) > 1e-2f);

        _output.WriteLine($"CUBICSPLINE: max abs error {maxError:E3}.");
    }

    private static ModelRoot CreateTwoNodeModel(out Node root, out Node child)
    {
        ModelRoot model = ModelRoot.CreateModel();
        Scene scene = model.UseScene("sampler-paths");
        model.DefaultScene = scene;
        root = scene.CreateNode("root");
        root.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
            new Vector3(1f, 1f, 1f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f),
            new Vector3(0.5f, 0f, -1f));
        child = root.CreateNode("child");
        child.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
            new Vector3(0.9f, 1.1f, 1f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.4f),
            new Vector3(0f, 0f, 2f));
        return model;
    }

    private static Quaternion[] RotationValues() =>
    [
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f),
        Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1f, 1f, 0f)), 1.1f),
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.7f)
    ];

    private static Vector3[] TranslationValues() =>
    [
        new Vector3(0f, 0f, 0f),
        new Vector3(2f, 1f, -0.5f),
        new Vector3(-1f, 3f, 4f)
    ];

    private static Vector3[] ScaleValues() =>
    [
        new Vector3(1f, 1f, 1f),
        new Vector3(2f, 0.5f, 1.5f),
        new Vector3(0.8f, 1.2f, 3f)
    ];

    private static Dictionary<float, T> Keys<T>(T[] values)
    {
        Dictionary<float, T> keys = [];
        for (int i = 0; i < KeyTimes.Length; i++)
        {
            keys[KeyTimes[i]] = values[i];
        }

        return keys;
    }

    private static void AssertInterpolation(Animation animation, Node node, AnimationInterpolationMode expected)
    {
        Assert.Equal(expected, animation.FindRotationChannel(node).GetRotationSampler().InterpolationMode);
        Assert.Equal(expected, animation.FindTranslationChannel(node).GetTranslationSampler().InterpolationMode);
    }

    private static float[] CreateSampleTimes(float[] keys)
    {
        List<float> times = [];
        for (int i = 0; i < keys.Length; i++)
        {
            times.Add(keys[i]);
            times.Add(keys[i] - 1e-4f);
            times.Add(keys[i] + 1e-4f);
            if (i + 1 < keys.Length)
            {
                float span = keys[i + 1] - keys[i];
                times.Add(keys[i] + span * 0.1f);
                times.Add(keys[i] + span * 0.5f);
                times.Add(keys[i] + span * 0.9f);
            }
        }

        float end = keys[^1];
        times.AddRange([-1f, 0f, keys[0] * 0.5f, end + 0.01f, end + 1f, 1000f]);
        return [.. times];
    }

    // Applies the clip with a fresh pose per sample time (nodes the clip leaves alone stay at rest,
    // which GetWorldMatrix(animation, time) assumes) and also with one pose reused across the
    // times, and checks both against SharpGLTF. Returns the largest absolute matrix difference.
    private static float AssertPoseMatchesSharpGltf(ModelRoot model, Animation animation, IEnumerable<float> times)
    {
        float maxError = 0f;
        AnimatedModelPose reused = new(model);
        foreach (float time in times)
        {
            AnimatedModelPose fresh = new(model);
            fresh.Apply(animation, time);
            reused.Apply(animation, time);
            foreach (AnimatedModelPose pose in new[] { fresh, reused })
            {
                foreach (Node node in model.LogicalNodes)
                {
                    if (animation.FindChannels(node).Any())
                    {
                        maxError = MathF.Max(maxError, AssertClose(
                            node.GetLocalTransform(animation, time).Matrix,
                            pose.GetNodeLocalMatrix(node.LogicalIndex),
                            $"'{animation.Name}' t={time} local node {node.Name}"));
                    }

                    maxError = MathF.Max(maxError, AssertClose(
                        node.GetWorldMatrix(animation, time),
                        pose.GetNodeWorldMatrix(node.LogicalIndex),
                        $"'{animation.Name}' t={time} world node {node.Name}"));
                }
            }
        }

        return maxError;
    }

    private static float AssertClose(Matrix4x4 expected, Matrix4x4 actual, string context)
    {
        float magnitude = 1f;
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                magnitude = MathF.Max(magnitude, MathF.Abs(expected[row, column]));
            }
        }

        float maxDifference = 0f;
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                float difference = MathF.Abs(expected[row, column] - actual[row, column]);
                maxDifference = MathF.Max(maxDifference, difference);
                Assert.True(
                    difference <= Tolerance * magnitude,
                    $"{context} [{row},{column}]: SharpGLTF {expected[row, column]} but pose {actual[row, column]}.");
            }
        }

        return maxDifference;
    }
}
