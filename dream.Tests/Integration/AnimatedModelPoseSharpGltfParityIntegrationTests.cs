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
public class AnimatedModelPoseSharpGltfParityIntegrationTests
{
    // Same relative tolerance as AnimatedModelAllocationTests: 1e-5 scaled by the matrix's
    // largest entry when that is above 1 (the wolf armature carries a scale of about 100).
    private const float Tolerance = 1e-5f;

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

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void Every_Clip_At_Keyframes_Midpoints_And_Outside_The_Clip_Matches_SharpGltf(string modelPath)
    {
        ModelRoot reference = ModelRoot.Load(modelPath);
        ModelRoot posed = ModelRoot.Load(modelPath);
        Assert.NotEmpty(reference.LogicalAnimations);
        ErrorTracker error = new();
        int samples = 0;

        for (int a = 0; a < reference.LogicalAnimations.Count; a++)
        {
            Animation referenceAnimation = reference.LogicalAnimations[a];
            Animation posedAnimation = posed.LogicalAnimations[a];
            // A fresh pose per clip: nodes the clip does not animate stay at rest, which is what
            // SharpGLTF's GetWorldMatrix(animation, time) assumes for them.
            AnimatedModelPose pose = new(posed);

            foreach (float time in CreateSampleTimes(referenceAnimation))
            {
                pose.Apply(posedAnimation, time);
                AssertPoseMatchesAnimation(reference, referenceAnimation, time, pose, error, $"clip '{referenceAnimation.Name}' t={time}");
                samples++;
            }
        }

        _output.WriteLine($"{Path.GetFileName(modelPath)}: {samples} samples, max abs error {error.MaxAbsolute:E3}, max relative error {error.MaxRelative:E3} ({error.Where}).");
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void Looped_And_OneShot_Playback_Past_The_Clip_End_Matches_SharpGltf_At_The_Played_Time(string modelPath)
    {
        ModelRoot reference = ModelRoot.Load(modelPath);
        ModelRoot posed = ModelRoot.Load(modelPath);
        ErrorTracker error = new();

        for (int a = 0; a < reference.LogicalAnimations.Count; a++)
        {
            Animation referenceAnimation = reference.LogicalAnimations[a];
            Animation posedAnimation = posed.LogicalAnimations[a];
            float duration = referenceAnimation.Duration;
            Assert.True(duration > 0f, $"Clip '{referenceAnimation.Name}' has no duration.");

            // Loop: play 2.5 clip lengths in uneven steps. Every step the playback time has
            // wrapped back inside the clip, and the pose equals SharpGLTF at the wrapped time.
            AnimatedModelPose loopPose = new(posed);
            AnimationPlaybackState loop = AnimationPlaybackState.Stopped.Play(referenceAnimation.Name, AnimationPlaybackMode.Loop);
            float played = 0f;
            int step = 0;
            bool wrapped = false;
            while (played < duration * 2.5f)
            {
                float delta = duration * (step % 3 == 0 ? 0.137f : 0.071f);
                played += delta;
                step++;
                loop = loop.Advance(delta, duration, out bool loopFinished);
                Assert.False(loopFinished);
                Assert.True(loop.IsPlaying);
                Assert.InRange(loop.TimeSeconds, 0f, duration);
                wrapped |= played > duration;

                float expectedTime = played % duration;
                Assert.True(
                    MathF.Abs(expectedTime - loop.TimeSeconds) <= 1e-4f * MathF.Max(1f, duration) ||
                    MathF.Abs(MathF.Abs(expectedTime - loop.TimeSeconds) - duration) <= 1e-4f * MathF.Max(1f, duration),
                    $"Loop of '{referenceAnimation.Name}' at {played}s played reports {loop.TimeSeconds}s, expected {expectedTime}s.");

                loopPose.Apply(posedAnimation, loop.TimeSeconds);
                AssertPoseMatchesAnimation(reference, referenceAnimation, loop.TimeSeconds, loopPose, error, $"loop '{referenceAnimation.Name}' played={played}");
            }

            Assert.True(wrapped);

            // One-shot: playing past the end finishes and holds the last frame.
            AnimatedModelPose oneShotPose = new(posed);
            AnimationPlaybackState oneShot = AnimationPlaybackState.Stopped.Play(referenceAnimation.Name, AnimationPlaybackMode.OneShot);
            oneShot = oneShot.Advance(duration * 0.6f, duration, out bool earlyFinished);
            Assert.False(earlyFinished);
            oneShot = oneShot.Advance(duration * 0.6f, duration, out bool finished);
            Assert.True(finished);
            Assert.False(oneShot.IsPlaying);
            Assert.Equal(duration, oneShot.TimeSeconds);
            oneShotPose.Apply(posedAnimation, oneShot.TimeSeconds);
            AssertPoseMatchesAnimation(reference, referenceAnimation, duration, oneShotPose, error, $"one-shot end '{referenceAnimation.Name}'");

            // Past the end without wrapping and before the start, both pose and SharpGLTF clamp.
            oneShotPose.Apply(posedAnimation, duration * 3f + 0.5f);
            AssertPoseMatchesAnimation(reference, referenceAnimation, duration, oneShotPose, error, $"clamped end '{referenceAnimation.Name}'");
            oneShotPose.Apply(posedAnimation, -duration - 1f);
            AssertPoseMatchesAnimation(reference, referenceAnimation, 0f, oneShotPose, error, $"clamped start '{referenceAnimation.Name}'");
        }

        _output.WriteLine($"{Path.GetFileName(modelPath)}: playback max abs error {error.MaxAbsolute:E3}, max relative error {error.MaxRelative:E3} ({error.Where}).");
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void Switching_Clips_Mid_Clip_Keeps_The_Previous_Clips_Channels_Like_Gltf_Node_Updates(string modelPath)
    {
        ModelRoot reference = ModelRoot.Load(modelPath);
        ModelRoot posed = ModelRoot.Load(modelPath);
        AnimatedModelPose pose = new(posed);
        ErrorTracker error = new();
        int clipCount = reference.LogicalAnimations.Count;
        Assert.True(clipCount >= 2, "Clip switching needs at least two clips.");

        // Every clip is played part way, then interrupted by the next one (wrapping back to the
        // first), twice round, at a different fraction of each clip each time.
        for (int round = 0; round < 2 * clipCount + 1; round++)
        {
            int a = round % clipCount;
            Animation referenceAnimation = reference.LogicalAnimations[a];
            Animation posedAnimation = posed.LogicalAnimations[a];
            float fraction = 0.15f + 0.7f * ((round * 0.37f) % 1f);
            float[] times = [0f, referenceAnimation.Duration * fraction * 0.5f, referenceAnimation.Duration * fraction];

            foreach (float time in times)
            {
                ApplyToGltfNodes(reference, referenceAnimation, time);
                pose.Apply(posedAnimation, time);
                for (int i = 0; i < reference.LogicalNodes.Count; i++)
                {
                    error.Check(reference.LogicalNodes[i].WorldMatrix, pose.GetNodeWorldMatrix(i), $"switch round {round} clip '{referenceAnimation.Name}' t={time} node {i}");
                }

                AssertJointMatricesMatch(reference, node => node.WorldMatrix, pose, error, $"switch round {round} clip '{referenceAnimation.Name}' t={time}");
            }
        }

        _output.WriteLine($"{Path.GetFileName(modelPath)}: clip-switch max abs error {error.MaxAbsolute:E3}, max relative error {error.MaxRelative:E3} ({error.Where}).");
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void Rotation_Keys_In_Opposite_Hemispheres_Interpolate_Along_The_Shortest_Arc(string modelPath)
    {
        ModelRoot reference = ModelRoot.Load(modelPath);
        ModelRoot posed = ModelRoot.Load(modelPath);
        int oppositePairs = 0;
        int checkedSamples = 0;
        float maxRotationError = 0f;

        for (int a = 0; a < reference.LogicalAnimations.Count; a++)
        {
            Animation referenceAnimation = reference.LogicalAnimations[a];
            AnimatedModelPose pose = new(posed);
            foreach (Node node in reference.LogicalNodes)
            {
                IAnimationSampler<Quaternion>? sampler = referenceAnimation.FindRotationChannel(node)?.GetRotationSampler();
                if (sampler == null || sampler.InterpolationMode != AnimationInterpolationMode.LINEAR)
                {
                    continue;
                }

                (float Key, Quaternion Value)[] keys = sampler.GetLinearKeys().ToArray();
                for (int k = 0; k + 1 < keys.Length; k++)
                {
                    if (Quaternion.Dot(keys[k].Value, keys[k + 1].Value) >= 0f || keys[k + 1].Key <= keys[k].Key)
                    {
                        continue;
                    }

                    oppositePairs++;
                    foreach (float amount in new[] { 0.25f, 0.5f, 0.75f })
                    {
                        float time = keys[k].Key + (keys[k + 1].Key - keys[k].Key) * amount;
                        pose.Apply(posed.LogicalAnimations[a], time);
                        Quaternion expected = ShortestArcSlerp(keys[k].Value, keys[k + 1].Value, amount);
                        Quaternion actual = ReadRotation(pose.GetNodeLocalMatrix(node.LogicalIndex));
                        maxRotationError = MathF.Max(maxRotationError, RotationAngleBetween(expected, actual));
                        Assert.True(
                            RotationAngleBetween(expected, actual) < 1e-3f,
                            $"Clip '{referenceAnimation.Name}' node {node.LogicalIndex} at t={time}: pose rotation {actual} is not on the shortest arc {expected}.");
                        AssertClose(node.GetLocalTransform(referenceAnimation, time).Matrix, pose.GetNodeLocalMatrix(node.LogicalIndex), new ErrorTracker(), $"local '{referenceAnimation.Name}' node {node.LogicalIndex}");
                        checkedSamples++;
                    }
                }
            }
        }

        _output.WriteLine($"{Path.GetFileName(modelPath)}: {oppositePairs} rotation key pairs with dot < 0, {checkedSamples} samples, max rotation error {maxRotationError:E3} rad.");
    }

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

    private static IEnumerable<float> CreateSampleTimes(Animation animation)
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

    private static IEnumerable<float> GetKeyTimes(AnimationChannel channel)
    {
        return channel.TargetNodePath switch
        {
            PropertyPath.rotation => channel.GetRotationSampler().GetLinearKeys().Select(key => key.Key),
            PropertyPath.translation => channel.GetTranslationSampler().GetLinearKeys().Select(key => key.Key),
            PropertyPath.scale => channel.GetScaleSampler().GetLinearKeys().Select(key => key.Key),
            _ => Array.Empty<float>()
        };
    }

    private static void ApplyToGltfNodes(ModelRoot model, Animation animation, float time)
    {
        foreach (Node node in model.LogicalNodes)
        {
            if (animation.FindChannels(node).Any())
            {
                node.LocalTransform = node.GetLocalTransform(animation, time);
            }
        }
    }

    private static void AssertPoseMatchesAnimation(
        ModelRoot reference,
        Animation animation,
        float time,
        AnimatedModelPose pose,
        ErrorTracker error,
        string context)
    {
        for (int i = 0; i < reference.LogicalNodes.Count; i++)
        {
            Node node = reference.LogicalNodes[i];
            if (animation.FindChannels(node).Any())
            {
                error.Check(node.GetLocalTransform(animation, time).Matrix, pose.GetNodeLocalMatrix(i), $"{context} local node {i}");
            }

            error.Check(node.GetWorldMatrix(animation, time), pose.GetNodeWorldMatrix(i), $"{context} world node {i}");
        }

        AssertJointMatricesMatch(reference, node => node.GetWorldMatrix(animation, time), pose, error, context);
    }

    private static void AssertJointMatricesMatch(
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

    private static void AssertClose(Matrix4x4 expected, Matrix4x4 actual, ErrorTracker error, string context)
    {
        error.Check(expected, actual, context);
    }

    private static Quaternion ReadRotation(Matrix4x4 local)
    {
        Assert.True(Matrix4x4.Decompose(local, out _, out Quaternion rotation, out _));
        return rotation;
    }

    // Independent of System.Numerics.Quaternion.Slerp, which both the pose and SharpGLTF use.
    private static Quaternion ShortestArcSlerp(Quaternion from, Quaternion to, float amount)
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
    private static float RotationAngleBetween(Quaternion left, Quaternion right)
    {
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(left), Quaternion.Normalize(right)));
        return 2f * MathF.Acos(MathF.Min(1f, dot));
    }

    private static float DegreesToRadians(float degrees) => degrees * MathF.PI / 180f;

    private sealed class ErrorTracker
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
