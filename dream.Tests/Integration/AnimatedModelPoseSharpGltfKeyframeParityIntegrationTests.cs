using SharpGLTF.Schema2;
using Xunit;
using Xunit.Abstractions;
using Dream.Rendering.Models;
using Dream.Tests.Rendering.Models;
using static Dream.Tests.Integration.AnimatedModelPoseSharpGltfParityIntegrationTests;

namespace Dream.Tests.Integration;

// AnimatedModelPoseSharpGltfParityIntegrationTests' per-keyframe sweep: every clip at every key,
// between keys, before the clip starts and after it ends. SharpGLTF's own evaluation walks every
// node's parents per sample, so the wolf's 24 clips alone took most of the suite's critical path.
// The clips are dealt round-robin over the concrete classes below (clip index modulo ShardCount);
// each class is its own xUnit collection, so the shards run in parallel, and together they cover
// every clip of every model exactly once.
public abstract class AnimatedModelPoseSharpGltfKeyframeParityIntegrationTests : IClassFixture<GltfModelRootFixture>
{
    public const int ShardCount = 3;

    private readonly ITestOutputHelper _output;
    private readonly GltfModelRootFixture _models;

    protected AnimatedModelPoseSharpGltfKeyframeParityIntegrationTests(ITestOutputHelper output, GltfModelRootFixture models)
    {
        _output = output;
        _models = models;
    }

    protected abstract int Shard { get; }

    [Theory]
    [MemberData(nameof(ModelPaths), MemberType = typeof(AnimatedModelPoseSharpGltfParityIntegrationTests))]
    public void Every_Clip_At_Keyframes_Midpoints_And_Outside_The_Clip_Matches_SharpGltf(string modelPath)
    {
        ModelRoot reference = _models.Get(modelPath, "reference");
        ModelRoot posed = _models.Get(modelPath, "posed");
        Assert.NotEmpty(reference.LogicalAnimations);
        Assert.InRange(Shard, 0, ShardCount - 1);
        ErrorTracker error = new();
        int clips = 0;
        int samples = 0;

        for (int a = Shard; a < reference.LogicalAnimations.Count; a += ShardCount)
        {
            Animation referenceAnimation = reference.LogicalAnimations[a];
            Animation posedAnimation = posed.LogicalAnimations[a];
            // A fresh pose per clip: nodes the clip does not animate stay at rest, which is what
            // SharpGLTF's GetWorldMatrix(animation, time) assumes for them.
            AnimatedModelPose pose = new(posed);
            clips++;

            foreach (float time in CreateSampleTimes(referenceAnimation))
            {
                pose.Apply(posedAnimation, time);
                AssertPoseMatchesAnimation(reference, referenceAnimation, time, pose, error, $"clip '{referenceAnimation.Name}' t={time}");
                samples++;
            }
        }

        _output.WriteLine($"{Path.GetFileName(modelPath)} shard {Shard}/{ShardCount}: {clips} clips, {samples} samples, max abs error {error.MaxAbsolute:E3}, max relative error {error.MaxRelative:E3} ({error.Where}).");
    }
}

public sealed class AnimatedModelPoseSharpGltfKeyframeParityShard0IntegrationTests(ITestOutputHelper output, GltfModelRootFixture models)
    : AnimatedModelPoseSharpGltfKeyframeParityIntegrationTests(output, models)
{
    protected override int Shard => 0;
}

public sealed class AnimatedModelPoseSharpGltfKeyframeParityShard1IntegrationTests(ITestOutputHelper output, GltfModelRootFixture models)
    : AnimatedModelPoseSharpGltfKeyframeParityIntegrationTests(output, models)
{
    protected override int Shard => 1;
}

public sealed class AnimatedModelPoseSharpGltfKeyframeParityShard2IntegrationTests(ITestOutputHelper output, GltfModelRootFixture models)
    : AnimatedModelPoseSharpGltfKeyframeParityIntegrationTests(output, models)
{
    protected override int Shard => 2;
}
