using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Models;

// GPU-4: one immutable AnimatedModelSkeleton per model and one AnimatedModelInstance per actor.
// GL is not available in tests, so the instances run over skeletons from AnimatedModelData.Load,
// which is exactly what AnimatedModel shares with its instances (AnimatedModel.Skeleton).
public class AnimatedModelInstanceTests
{
    private const float FrameSeconds = 1f / 60f;

    // Loaded once per asset on the thread pool and only read by the tests below.
    private static readonly Lazy<AnimatedModelData> Wolf = new(() => AnimatedModelData.Load(WolfAssetDefinition.ModelPath));
    private static readonly Lazy<AnimatedModelData> Player = new(() => AnimatedModelData.Load(PlayerModelAssetDefinition.ModelPath));

    public static TheoryData<string> ModelPaths() => new()
    {
        WolfAssetDefinition.ModelPath,
        PlayerModelAssetDefinition.ModelPath
    };

    [Fact]
    public void Three_Wolves_Over_One_Second_Each_Reach_One_Second_Not_Three()
    {
        AnimatedModelSkeleton skeleton = Wolf.Value.Skeleton;
        EnemyVisualStateCache cache = new();
        int[] wolves = [2001, 2002, 2003];

        for (int frame = 0; frame < 60; frame++)
        {
            foreach (int wolf in wolves)
            {
                EnemyRenderer.AdvanceInstance(cache.GetOrAdd(wolf, skeleton), WolfAssetDefinition.MoveClip, FrameSeconds);
            }
        }

        float duration = skeleton.Clips[WolfAssetDefinition.MoveClip].Duration;
        float oneSecond = Advance(WolfAssetDefinition.MoveClip, duration, FrameSeconds, steps: 60);
        float threeSeconds = Advance(WolfAssetDefinition.MoveClip, duration, FrameSeconds, steps: 180);
        Assert.NotEqual(oneSecond, threeSeconds);

        AnimatedModelInstance[] instances = wolves.Select(wolf => cache.GetOrAdd(wolf).Instance!).ToArray();
        Assert.Equal(3, instances.Distinct().Count());
        foreach (AnimatedModelInstance instance in instances)
        {
            Assert.Same(skeleton, instance.Skeleton);
            Assert.Equal(WolfAssetDefinition.MoveClip, instance.CurrentClipName);
            Assert.Equal(oneSecond, instance.TimeSeconds);
        }
    }

    [Fact]
    public void Two_Enemy_Instances_Advance_Independently()
    {
        AnimatedModelSkeleton skeleton = Wolf.Value.Skeleton;
        EnemyVisualStateCache cache = new();
        EnemyVisualState first = cache.GetOrAdd(1, skeleton);
        EnemyVisualState second = cache.GetOrAdd(2, skeleton);

        EnemyRenderer.AdvanceInstance(first, WolfAssetDefinition.MoveClip, 0.1f);
        EnemyRenderer.AdvanceInstance(second, WolfAssetDefinition.MoveClip, 0.1f);
        for (int i = 0; i < 5; i++)
        {
            EnemyRenderer.AdvanceInstance(first, WolfAssetDefinition.MoveClip, 0.1f);
        }

        Assert.Equal(Advance(WolfAssetDefinition.MoveClip, skeleton.Clips[WolfAssetDefinition.MoveClip].Duration, 0.1f, 6), first.Instance!.TimeSeconds);
        Assert.Equal(0.1f, second.Instance!.TimeSeconds);

        // Switching one enemy's clip leaves the other's clip and time alone.
        EnemyRenderer.AdvanceInstance(second, WolfAssetDefinition.AttackClip, 0f);
        Assert.Equal(WolfAssetDefinition.MoveClip, first.Instance.CurrentClipName);
        Assert.Equal(WolfAssetDefinition.AttackClip, second.Instance.CurrentClipName);
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void Different_Clip_Phases_Give_Different_Poses(string modelPath)
    {
        AnimatedModelData data = Load(modelPath);
        AnimatedModelSkeleton skeleton = data.Skeleton;
        Animation clip = skeleton.Clips.Values.First(animation => data.Pose.GetAnimatedNodeCount(animation) > 0 && animation.Duration > 0.2f);
        AnimatedModelInstance early = new(skeleton);
        AnimatedModelInstance late = new(skeleton);
        early.PlayLoop(clip.Name);
        late.PlayLoop(clip.Name);

        early.Update(clip.Duration * 0.1f);
        late.Update(clip.Duration * 0.55f);

        Assert.NotEqual(early.TimeSeconds, late.TimeSeconds);
        Assert.True(PosesDiffer(early.Pose, late.Pose), "Two phases of one clip produced the same pose.");
        if (skeleton.HasSkin)
        {
            Matrix4 meshWorld = Matrix4.Identity;
            Assert.False(early.ComputeJointMatrices(meshWorld).SequenceEqual(late.ComputeJointMatrices(meshWorld).ToArray()));
        }

        // The rest pose the shared model draws is untouched by either instance.
        AnimatedModelPose rest = new(skeleton);
        for (int i = 0; i < rest.NodeCount; i++)
        {
            Assert.Equal(rest.GetNodeWorldMatrix(i), data.Pose.GetNodeWorldMatrix(i));
        }
    }

    // The pre-split AnimatedModel path, inlined: one AnimationPlaybackState advanced by
    // deltaTime * AnimationSpeed, then AnimatedModelPose.Apply at the played time, and joint
    // matrices composed per joint from the pose. The instance must match it bit for bit.
    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void Single_Instance_Pose_And_Joints_Are_Bit_Identical_To_The_Old_Model_Path(string modelPath)
    {
        AnimatedModelData data = Load(modelPath);
        ModelRoot reference = ModelRoot.Load(modelPath);
        AnimatedModelPose oldPose = new(reference);
        AnimatedModelInstance instance = new(data.Skeleton);
        AnimatedModelSkinJoint[] joints = data.Skeleton.SkinJoints.ToArray();
        float[] steps = [0f, 0.016f, 0.05f, 0.137f, 0.3f, 0.071f, 0.9f, 1.7f];

        foreach (Animation clip in data.Skeleton.Clips.Values)
        {
            Animation referenceClip = reference.LogicalAnimations[clip.LogicalIndex];
            AnimationPlaybackState playback = AnimationPlaybackState.Stopped.Play(clip.Name, AnimationPlaybackMode.Loop);
            instance.PlayLoop(clip.Name);
            foreach (float step in steps)
            {
                playback = playback.Advance(step * 1.0f, referenceClip.Duration, out _);
                oldPose.Apply(referenceClip, playback.TimeSeconds);
                instance.Update(step);

                Assert.Equal(playback.TimeSeconds, instance.TimeSeconds);
                for (int node = 0; node < oldPose.NodeCount; node++)
                {
                    Assert.Equal(oldPose.GetNodeLocalMatrix(node), instance.Pose.GetNodeLocalMatrix(node));
                    Assert.Equal(oldPose.GetNodeWorldMatrix(node), instance.Pose.GetNodeWorldMatrix(node));
                }

                for (int renderable = 0; renderable < oldPose.RenderableNodeCount; renderable++)
                {
                    Matrix4 meshWorld = oldPose.GetRenderableNodeWorldMatrix(renderable).ToOpenTK();
                    Assert.Equal(meshWorld, instance.Pose.GetRenderableNodeWorldMatrix(renderable).ToOpenTK());
                    ReadOnlySpan<Matrix4> actual = instance.ComputeJointMatrices(meshWorld);
                    Assert.Equal(joints.Length, actual.Length);
                    for (int j = 0; j < joints.Length; j++)
                    {
                        Matrix4 expected = AnimatedModelTransformComposer.ComposeSkinJointMatrix(
                            meshWorld,
                            oldPose.GetNodeWorldMatrix(joints[j].NodeIndex).ToOpenTK(),
                            joints[j].InverseBindMatrix);
                        Assert.Equal(expected, actual[j]);
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ModelPaths))]
    public void Instance_Update_And_Draw_Prep_Allocate_Nothing_After_Warmup(string modelPath)
    {
        AnimatedModelData data = Load(modelPath);
        AnimatedModelInstance instance = new(data.Skeleton);
        Animation clip = data.Skeleton.Clips.Values.First(animation => data.Pose.GetAnimatedNodeCount(animation) > 0);
        instance.PlayLoop(clip.Name);
        float checksum = 0f;

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            instance.Update(FrameSeconds);
            for (int i = 0; i < instance.Pose.RenderableNodeCount; i++)
            {
                Matrix4 meshWorld = instance.Pose.GetRenderableNodeWorldMatrix(i).ToOpenTK();
                ReadOnlySpan<Matrix4> joints = instance.ComputeJointMatrices(meshWorld);
                checksum += joints.Length > 0 ? joints[0].M44 : meshWorld.M44;
            }
        });

        Assert.Equal(0, bytes);
        Assert.NotEqual(0f, checksum);
    }

    [Fact]
    public void Enemy_Frame_With_Steady_Enemies_Allocates_Nothing_After_Warmup()
    {
        AnimatedModelSkeleton skeleton = Wolf.Value.Skeleton;
        EnemyVisualStateCache cache = new();
        HashSet<int> current = [1, 2, 3, 4, 5];

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            foreach (int id in current)
            {
                EnemyVisualState state = cache.GetOrAdd(id, skeleton);
                EnemyRenderer.AdvanceInstance(state, id % 2 == 0 ? WolfAssetDefinition.MoveClip : WolfAssetDefinition.IdleClip, FrameSeconds);
                state.Instance!.ComputeJointMatrices(Matrix4.Identity);
            }

            cache.PruneExcept(current);
        });

        Assert.Equal(0, bytes);
        Assert.Equal(5, cache.Count);
    }

    [Fact]
    public void Released_Enemy_Instance_Is_Pooled_And_Reused_Reset_For_The_Next_Enemy()
    {
        AnimatedModelSkeleton skeleton = Wolf.Value.Skeleton;
        EnemyVisualStateCache cache = new();
        EnemyVisualState leaving = cache.GetOrAdd(10, skeleton);
        EnemyRenderer.AdvanceInstance(leaving, WolfAssetDefinition.MoveClip, 0.4f);
        AnimatedModelInstance used = leaving.Instance!;

        cache.PruneExcept([]);

        Assert.Null(leaving.Instance);
        Assert.Equal(1, cache.PooledInstanceCount);

        EnemyVisualState arriving = cache.GetOrAdd(11, skeleton);
        Assert.NotSame(leaving, arriving);
        Assert.Same(used, arriving.Instance);
        Assert.Equal(0, cache.PooledInstanceCount);
        Assert.Null(arriving.CurrentClipName);
        Assert.Null(arriving.Instance!.CurrentClipName);
        Assert.Equal(0f, arriving.Instance.TimeSeconds);
        AnimatedModelPose rest = new(skeleton);
        for (int i = 0; i < rest.NodeCount; i++)
        {
            Assert.Equal(rest.GetNodeWorldMatrix(i), arriving.Instance.Pose.GetNodeWorldMatrix(i));
        }

        // The reused instance starts its clip from zero.
        EnemyRenderer.AdvanceInstance(arriving, WolfAssetDefinition.MoveClip, 0.1f);
        Assert.Equal(0.1f, arriving.Instance.TimeSeconds);
    }

    [Fact]
    public void Enemy_State_Rebinds_When_The_Model_Is_Reloaded()
    {
        AnimatedModelSkeleton first = Wolf.Value.Skeleton;
        AnimatedModelSkeleton reloaded = AnimatedModelData.Load(WolfAssetDefinition.ModelPath).Skeleton;
        Assert.NotSame(first, reloaded);
        EnemyVisualStateCache cache = new();
        EnemyVisualState state = cache.GetOrAdd(1, first);
        EnemyRenderer.AdvanceInstance(state, WolfAssetDefinition.MoveClip, 0.2f);

        cache.GetOrAdd(1, reloaded);

        Assert.Same(reloaded, state.Instance!.Skeleton);
        Assert.Null(state.CurrentClipName);
        // The rebound instance really animates over the reloaded skeleton.
        EnemyRenderer.AdvanceInstance(state, WolfAssetDefinition.MoveClip, 0.1f);
        state.Instance.Update(0.1f);
        Assert.Equal(WolfAssetDefinition.MoveClip, state.Instance.CurrentClipName);
        Assert.True(state.Instance.TimeSeconds > 0f);
        // The stale instance is not handed to an enemy of the reloaded model.
        Assert.Same(reloaded, cache.GetOrAdd(2, reloaded).Instance!.Skeleton);
    }

    [Fact]
    public void Player_Clip_Tracker_Replays_A_Recorded_Clip_On_A_New_Instance()
    {
        AnimatedModelSkeleton skeleton = Player.Value.Skeleton;
        PlayerAnimationClipTracker tracker = new();
        AnimatedModelInstance first = new(skeleton);

        Assert.True(tracker.TryApplyClip(7, PlayerModelAssetDefinition.IdleClip, first));
        Assert.False(tracker.TryApplyClip(7, PlayerModelAssetDefinition.IdleClip, first));

        // The actor's handle was released far away and requested again: a fresh, stopped instance.
        AnimatedModelInstance second = new(skeleton);
        Assert.True(tracker.TryApplyClip(7, PlayerModelAssetDefinition.IdleClip, second));
        Assert.True(second.IsPlayingLoop(PlayerModelAssetDefinition.IdleClip));
    }

    // The joint array goes up in one glUniformMatrix4fv call per skinned draw, counted once,
    // instead of one SetMatrix4 per joint.
    [Fact]
    public void Joint_Matrices_Are_Uploaded_In_One_Array_Call_Per_Draw()
    {
        string source = StripComments(Read("dream/Rendering/Models/AnimatedModel.cs"));
        string applySkinning = Method(source, "private void ApplySkinning(");
        string upload = Method(source, "private static void UploadJointMatrices(");

        Assert.Single(Regex.Matches(source, @"GL\.UniformMatrix4\("));
        Assert.Matches(new Regex(@"GL\.UniformMatrix4\(location, jointMatrices\.Length, false, ref "), upload);
        Assert.Single(Regex.Matches(upload, @"RenderCounters\.CountUniformUpload\(\)"));
        Assert.DoesNotMatch(new Regex(@"\b(for|foreach|while)\b"), upload);

        Assert.Single(Regex.Matches(applySkinning, @"UploadJointMatrices\("));
        Assert.DoesNotMatch(new Regex(@"\b(for|foreach|while)\b"), applySkinning);
        Assert.DoesNotContain("SetMatrix4", applySkinning);
        Assert.DoesNotContain("JointMatrixUniformNames)", applySkinning);
        Assert.DoesNotContain("new Matrix4[", applySkinning + upload);
    }

    [Fact]
    public void Instances_And_Handles_Own_No_GL_Resources_And_Never_Dispose_The_Shared_Model()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(AnimatedModelInstance)));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(AnimatedModelSkeleton)));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(AnimatedModelPose)));
        foreach (string file in new[] { "AnimatedModelInstance.cs", "AnimatedModelSkeleton.cs" })
        {
            string source = StripComments(Read($"dream/Rendering/Models/{file}"));
            Assert.DoesNotContain("GL.", source);
            Assert.DoesNotContain("OpenGL", source);
        }

        string handleDispose = Method(
            StripComments(Read("dream/Rendering/Core/ModelAssets.cs")),
            "public void Dispose()");
        Assert.DoesNotContain(".Dispose(", handleDispose);
        Assert.DoesNotContain("GL.", handleDispose);
    }

    [Fact]
    public void Player_Instance_Handles_Share_One_Decode_And_Report_Disposed_After_Loader_Teardown()
    {
        using ManualResetEventSlim release = new();
        int decodes = 0;
        AsyncResourceLoader<AnimatedModelData, AnimatedModel> loader = new(
            path =>
            {
                Interlocked.Increment(ref decodes);
                release.Wait(TimeSpan.FromSeconds(30));
                throw new InvalidOperationException("never uploaded in this test");
            },
            _ => throw new InvalidOperationException("never uploaded in this test"),
            new RecordingFaultSink(),
            "test");

        AnimatedModelInstanceHandle first = new(loader, "player.glb");
        AnimatedModelInstanceHandle second = new(loader, "player.glb");

        Assert.Equal(1, loader.PendingCount);
        Assert.Equal(0, loader.LiveInstanceCount);
        Assert.Equal(AsyncResourceState.Pending, first.State);
        Assert.False(first.TryGet(out _, out _));

        release.Set();
        loader.Dispose();

        Assert.Equal(AsyncResourceState.Disposed, first.State);
        Assert.Equal(AsyncResourceState.Disposed, second.State);
        Assert.False(second.TryGet(out _, out _));
        first.Dispose();
        first.Dispose();
        Assert.Equal(AsyncResourceState.Disposed, first.State);
        Assert.True(Volatile.Read(ref decodes) <= 1);
    }

    private static float Advance(string clipName, float duration, float step, int steps)
    {
        AnimationPlaybackState playback = AnimationPlaybackState.Stopped.Play(clipName, AnimationPlaybackMode.Loop);
        for (int i = 0; i < steps; i++)
        {
            playback = playback.Advance(step, duration, out _);
        }

        return playback.TimeSeconds;
    }

    private static bool PosesDiffer(AnimatedModelPose left, AnimatedModelPose right)
    {
        for (int i = 0; i < left.NodeCount; i++)
        {
            if (left.GetNodeWorldMatrix(i) != right.GetNodeWorldMatrix(i))
            {
                return true;
            }
        }

        return false;
    }

    private static AnimatedModelData Load(string modelPath) =>
        modelPath == WolfAssetDefinition.ModelPath ? Wolf.Value : Player.Value;

    private sealed class RecordingFaultSink : IRenderFaultSink
    {
        public void Report(string source, Exception exception)
        {
        }
    }

    private static string StripComments(string source) =>
        Regex.Replace(
            source,
            @"""(?:\\.|[^""\\\n])*""|//[^\n]*|/\*[\s\S]*?\*/",
            match => match.Value.StartsWith('"') ? match.Value : string.Empty);

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        throw new InvalidOperationException("Unbalanced braces.");
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
