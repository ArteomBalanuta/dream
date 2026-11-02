using SharpGLTF.Schema2;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Actors;

// ApplyFrame over a real instance of the player model's clips (no GL).
public class PlayerAnimationClipTrackerFrameTests
{
    private static AnimatedModelInstance CreateInstance()
    {
        ModelRoot model = ModelRoot.Load(PlayerModelAssetDefinition.ModelPath);
        Dictionary<string, Animation> clips = model.LogicalAnimations.ToDictionary(a => a.Name, a => a);
        return new AnimatedModelInstance(new AnimatedModelSkeleton(model, clips, skinJoints: null));
    }

    private static PlayerAnimationFrame Frame(string clip, PlayerClipMode mode, int serial = 0, float speed = 1f) =>
        new(clip, mode, serial, speed, 0f, 0f, 0f, 0f, false);

    [Fact]
    public void Loop_StartsOnce_AndFollowsTheFramePlaybackSpeed()
    {
        PlayerAnimationClipTracker tracker = new();
        AnimatedModelInstance instance = CreateInstance();

        tracker.ApplyFrame(1, Frame("walk", PlayerClipMode.Loop, speed: 1.4f), instance);
        instance.Update(0.1f);
        float timeAfterFirst = instance.TimeSeconds;
        tracker.ApplyFrame(1, Frame("walk", PlayerClipMode.Loop, speed: 1.4f), instance);

        Assert.True(instance.IsPlayingLoop("walk"));
        Assert.Equal(1.4f, instance.AnimationSpeed);
        Assert.Equal(timeAfterFirst, instance.TimeSeconds);
    }

    [Fact]
    public void OneShot_PlaysOncePerSerial_AndReturnsToTheLoopThatWasPlaying()
    {
        PlayerAnimationClipTracker tracker = new();
        AnimatedModelInstance instance = CreateInstance();
        tracker.ApplyFrame(1, Frame("idle", PlayerClipMode.Loop), instance);

        tracker.ApplyFrame(1, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 3), instance);
        Assert.Equal("attack-melee-right", instance.CurrentClipName);

        instance.Update(0.2f);
        float time = instance.TimeSeconds;
        tracker.ApplyFrame(1, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 3), instance);
        Assert.Equal(time, instance.TimeSeconds);

        instance.Update(1f);
        Assert.True(instance.IsPlayingLoop("idle"));

        tracker.ApplyFrame(1, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 4), instance);
        Assert.Equal(0f, instance.TimeSeconds);
        Assert.Equal("attack-melee-right", instance.CurrentClipName);
    }

    [Fact]
    public void Hold_StaysOnTheLastPose_AndALoopAfterwardsRevivesTheActor()
    {
        PlayerAnimationClipTracker tracker = new();
        AnimatedModelInstance instance = CreateInstance();
        tracker.ApplyFrame(1, Frame("idle", PlayerClipMode.Loop), instance);

        tracker.ApplyFrame(1, Frame("die", PlayerClipMode.Hold, serial: 1), instance);
        instance.Update(2f);
        System.Numerics.Matrix4x4 lying = instance.Pose.GetNodeWorldMatrix(1);
        instance.Update(2f);
        tracker.ApplyFrame(1, Frame("die", PlayerClipMode.Hold, serial: 1), instance);
        instance.Update(1f);

        Assert.False(instance.Playback.IsPlaying);
        Assert.Equal(lying, instance.Pose.GetNodeWorldMatrix(1));

        tracker.ApplyFrame(1, Frame("idle", PlayerClipMode.Loop, serial: 2), instance);
        Assert.True(instance.IsPlayingLoop("idle"));
    }

    [Fact]
    public void Respawn_AfterHold_RestoresNodesThatOnlyTheDeathClipAnimates()
    {
        PlayerAnimationClipTracker tracker = new();
        AnimatedModelInstance instance = CreateInstance();
        instance.Skeleton.TryGetClip("die", out Animation? die, out _);
        instance.Skeleton.TryGetClip("idle", out Animation? idle, out _);
        HashSet<int> idleNodes = instance.Skeleton.GetTracks(idle!).Select(t => t.NodeIndex).ToHashSet();
        int[] deathOnly = instance.Skeleton.GetTracks(die!).Select(t => t.NodeIndex).Where(n => !idleNodes.Contains(n)).ToArray();
        Assert.NotEmpty(deathOnly);

        AnimatedModelInstance rest = CreateInstance();
        tracker.ApplyFrame(1, Frame("idle", PlayerClipMode.Loop), instance);
        tracker.ApplyFrame(1, Frame("die", PlayerClipMode.Hold, serial: 1), instance);
        instance.Update(3f);
        Assert.Contains(deathOnly, n => instance.Pose.GetNodeLocalMatrix(n) != rest.Pose.GetNodeLocalMatrix(n));

        tracker.ApplyFrame(1, Frame("idle", PlayerClipMode.Loop, serial: 2), instance);
        instance.Update(0.1f);

        Assert.True(instance.IsPlayingLoop("idle"));
        foreach (int n in deathOnly)
        {
            Assert.Equal(rest.Pose.GetNodeLocalMatrix(n), instance.Pose.GetNodeLocalMatrix(n));
        }
    }

    [Fact]
    public void Remove_ForgetsTheActor_SoANewInstanceStartsItsClip()
    {
        PlayerAnimationClipTracker tracker = new();
        AnimatedModelInstance first = CreateInstance();
        tracker.ApplyFrame(1, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 1), first);

        tracker.Remove(1);
        AnimatedModelInstance second = CreateInstance();
        tracker.ApplyFrame(1, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 1), second);

        Assert.Equal("attack-melee-right", second.CurrentClipName);
    }
}
