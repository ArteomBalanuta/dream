using System.Numerics;
using SharpGLTF.Schema2;
using Xunit;
using Xunit.Abstractions;
using Dream.Rendering.Models;
using Dream.Tests.Rendering.Models;
using static Dream.Tests.Integration.AnimatedModelPoseSharpGltfParityIntegrationTests;

namespace Dream.Tests.Integration;

// AnimatedModelPoseSharpGltfParityIntegrationTests' clip-switch and shortest-arc rotation checks, in
// their own class so they run in parallel with the per-keyframe sweep and the playback checks.
// The clip-switch test loads its own reference (it writes node transforms); the shared models
// are only read; see the comment on the main class.
public class AnimatedModelPoseSharpGltfClipSwitchParityIntegrationTests : IClassFixture<GltfModelRootFixture>
{
    private readonly ITestOutputHelper _output;
    private readonly GltfModelRootFixture _models;

    public AnimatedModelPoseSharpGltfClipSwitchParityIntegrationTests(ITestOutputHelper output, GltfModelRootFixture models)
    {
        _output = output;
        _models = models;
    }

    [Theory]
    [MemberData(nameof(ModelPaths), MemberType = typeof(AnimatedModelPoseSharpGltfParityIntegrationTests))]
    public void Switching_Clips_Mid_Clip_Keeps_The_Previous_Clips_Channels_Like_Gltf_Node_Updates(string modelPath)
    {
        // ApplyToGltfNodes writes node transforms, so this reference is a private load.
        ModelRoot reference = ModelRoot.Load(modelPath);
        ModelRoot posed = _models.Get(modelPath, "posed");
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
    [MemberData(nameof(ModelPaths), MemberType = typeof(AnimatedModelPoseSharpGltfParityIntegrationTests))]
    public void Rotation_Keys_In_Opposite_Hemispheres_Interpolate_Along_The_Shortest_Arc(string modelPath)
    {
        ModelRoot reference = _models.Get(modelPath, "reference");
        ModelRoot posed = _models.Get(modelPath, "posed");
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
}
