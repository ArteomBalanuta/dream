using System.Numerics;
using SharpGLTF.Schema2;
using Xunit;
using Xunit.Abstractions;
using Dream.Rendering.Models;
using Dream.Tests.Rendering.Models;
using static Dream.Tests.Integration.AnimatedModelPoseSharpGltfParityIntegrationTests;

namespace Dream.Tests.Integration;

// AnimatedModelPoseSharpGltfParityIntegrationTests' playback checks (looped and one-shot playback
// past the clip end), in their own class so they run in parallel with the per-keyframe sweep. The shared "reference" and "posed" models are only read; see the
// comment on the main class.
public class AnimatedModelPoseSharpGltfPlaybackParityIntegrationTests : IClassFixture<GltfModelRootFixture>
{
    private readonly ITestOutputHelper _output;
    private readonly GltfModelRootFixture _models;

    public AnimatedModelPoseSharpGltfPlaybackParityIntegrationTests(ITestOutputHelper output, GltfModelRootFixture models)
    {
        _output = output;
        _models = models;
    }

    [Theory]
    [MemberData(nameof(ModelPaths), MemberType = typeof(AnimatedModelPoseSharpGltfParityIntegrationTests))]
    public void Looped_And_OneShot_Playback_Past_The_Clip_End_Matches_SharpGltf_At_The_Played_Time(string modelPath)
    {
        ModelRoot reference = _models.Get(modelPath, "reference");
        ModelRoot posed = _models.Get(modelPath, "posed");
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

}
