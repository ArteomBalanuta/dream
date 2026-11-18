using SharpGLTF.Schema2;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;

namespace Dream.Tests.BugHunt;

// BH-ANIM-3: a remote player far-released mid-swing came back frozen in the bind pose.
//
// RemotePlayerRenderer.ReleaseFarModels disposes the model handle and deliberately keeps the
// actor's clip record (Wave8AnimatedInstanceLifecycleTests states that intent: the actor is still
// active, it is the same player, so its gait and its clip carry over). The next TryGet therefore
// builds a brand-new AnimatedModelInstance in the Stopped state while the tracker still holds the
// swing's serial. The Loop branch handles that (IsPlayingLoop) and the Hold branch handles it
// (CurrentClipName), and the OneShot branch did not: it saw the matching serial, played nothing,
// and Update then returned early every frame, leaving the skeleton in its authored bind pose.
//
// Headless: a real player skeleton over SharpGLTF, no GL context.
public class AnimOneShotFreshInstanceTests
{
    private static AnimatedModelInstance CreateInstance()
    {
        ModelRoot model = ModelRoot.Load(PlayerModelAssetDefinition.ModelPath);
        Dictionary<string, Animation> clips = model.LogicalAnimations.ToDictionary(a => a.Name, a => a);
        return new AnimatedModelInstance(new AnimatedModelSkeleton(model, clips, skinJoints: null));
    }

    private static PlayerAnimationFrame Frame(string clip, PlayerClipMode mode, int serial = 0) =>
        new(clip, mode, serial, 1f, 0f, 0f, 0f, 0f, false);

    [Fact]
    public void AFreshInstanceUnderTheSameSwingSerial_StartsPlayingInsteadOfHoldingTheBindPose()
    {
        PlayerAnimationClipTracker tracker = new();
        AnimatedModelInstance released = CreateInstance();
        AnimatedModelInstance bind = CreateInstance();

        tracker.ApplyFrame(4, Frame("run", PlayerClipMode.Loop), released);
        tracker.ApplyFrame(4, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 7), released);
        released.Update(0.1f);

        // Far release, then back in range inside the 0.42 s swing window: a new instance, the same
        // recorded serial, and the state machine still reporting the same swing.
        AnimatedModelInstance returned = CreateInstance();
        tracker.ApplyFrame(4, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 7), returned);
        returned.Update(1f / 60f);

        Assert.True(returned.Playback.IsPlaying);
        Assert.Equal("attack-melee-right", returned.CurrentClipName);
        // Something is actually posed, rather than the skeleton sitting in its bind pose.
        Assert.True(returned.Skeleton.TryGetClip("attack-melee-right", out Animation? attack));
        int[] attackNodes = returned.Skeleton.GetTracks(attack!).Select(t => t.NodeIndex).ToArray();
        Assert.Contains(attackNodes, n => returned.Pose.GetNodeLocalMatrix(n) != bind.Pose.GetNodeLocalMatrix(n));
    }

    [Fact]
    public void ThePlayingInstanceIsStillNotRestarted_WhileTheSerialHoldsOrAfterTheSwingEnds()
    {
        PlayerAnimationClipTracker tracker = new();
        AnimatedModelInstance instance = CreateInstance();
        tracker.ApplyFrame(4, Frame("run", PlayerClipMode.Loop), instance);
        tracker.ApplyFrame(4, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 7), instance);

        // Mid-swing: a repeat of the same serial leaves the clip time alone.
        for (int frame = 0; frame < 6; frame++)
        {
            instance.Update(1f / 60f);
            float time = instance.TimeSeconds;
            tracker.ApplyFrame(4, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 7), instance);
            Assert.Equal(time, instance.TimeSeconds);
            Assert.Equal("attack-melee-right", instance.CurrentClipName);
        }

        // After the swing ends the instance is looping the gait again, which is playing, so the
        // held serial must not resurrect the swing.
        instance.Update(1f);
        Assert.True(instance.IsPlayingLoop("run"));
        for (int frame = 0; frame < 6; frame++)
        {
            tracker.ApplyFrame(4, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 7), instance);
            instance.Update(1f / 60f);
            Assert.True(instance.IsPlayingLoop("run"));
        }
    }
}
