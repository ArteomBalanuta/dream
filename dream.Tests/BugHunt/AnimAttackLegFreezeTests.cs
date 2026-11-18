using SharpGLTF.Schema2;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;

namespace Dream.Tests.BugHunt;

// BH-ANIM-1: an attack one-shot froze the legs mid-stride.
//
// AnimatedModelPose.Apply only writes the nodes of the clip being played, and the attack clips
// animate the torso, head and arms and no leg or root node. So a swing started while walking,
// running or sprinting left both legs holding whatever angle the gait cycle last sampled for the
// length of the swing. ApplyFrame already compensated for the same failure on the Hold -> non-Hold
// edge (respawn) and not on the Loop -> OneShot edge.
//
// Headless: a real player skeleton over SharpGLTF, no GL context.
public class AnimAttackLegFreezeTests
{
    private static AnimatedModelInstance CreateInstance()
    {
        ModelRoot model = ModelRoot.Load(PlayerModelAssetDefinition.ModelPath);
        Dictionary<string, Animation> clips = model.LogicalAnimations.ToDictionary(a => a.Name, a => a);
        return new AnimatedModelInstance(new AnimatedModelSkeleton(model, clips, skinJoints: null));
    }

    private static PlayerAnimationFrame Frame(string clip, PlayerClipMode mode, int serial = 0) =>
        new(clip, mode, serial, 1f, 0f, 0f, 0f, 0f, false);

    // The nodes the gait animates and the attack does not: the ones that could be stranded.
    private static int[] GaitOnlyNodes(AnimatedModelInstance instance, string gaitClip, string attackClip)
    {
        Assert.True(instance.Skeleton.TryGetClip(gaitClip, out Animation? gait));
        Assert.True(instance.Skeleton.TryGetClip(attackClip, out Animation? attack));
        HashSet<int> attackNodes = instance.Skeleton.GetTracks(attack!).Select(t => t.NodeIndex).ToHashSet();
        return instance.Skeleton.GetTracks(gait!).Select(t => t.NodeIndex).Where(n => !attackNodes.Contains(n)).ToArray();
    }

    [Theory]
    [InlineData("walk", "attack-melee-right")]
    [InlineData("run", "attack-melee-right")]
    [InlineData("sprint", "attack-melee-right")]
    [InlineData("run", "holding-right-shoot")]
    public void AnAttackStartedWhileMoving_LeavesTheNodesItDoesNotAnimateAtRest(string gaitClip, string attackClip)
    {
        PlayerAnimationClipTracker tracker = new();
        AnimatedModelInstance instance = CreateInstance();
        AnimatedModelInstance rest = CreateInstance();
        int[] gaitOnly = GaitOnlyNodes(instance, gaitClip, attackClip);
        // The asset fact the finding rests on: the attack clips really do leave the legs (and the
        // root) unanimated. Without this the test could pass by animating everything.
        Assert.NotEmpty(gaitOnly);

        // Walk far enough into the cycle to be well off neutral.
        tracker.ApplyFrame(1, Frame(gaitClip, PlayerClipMode.Loop), instance);
        instance.Update(0.35f);
        Assert.Contains(gaitOnly, n => instance.Pose.GetNodeLocalMatrix(n) != rest.Pose.GetNodeLocalMatrix(n));

        // Swing. Every node the swing does not drive must be back at the authored neutral stance,
        // for the whole swing and not just its first frame.
        tracker.ApplyFrame(1, Frame(attackClip, PlayerClipMode.OneShot, serial: 1), instance);
        for (int frame = 0; frame < 12; frame++)
        {
            instance.Update(1f / 60f);
            foreach (int node in gaitOnly)
            {
                Assert.Equal(rest.Pose.GetNodeLocalMatrix(node), instance.Pose.GetNodeLocalMatrix(node));
            }
        }
    }

    [Fact]
    public void AClipThatDoesAnimateANode_IsUnaffectedByTheReset()
    {
        PlayerAnimationClipTracker tracker = new();
        AnimatedModelInstance instance = CreateInstance();
        AnimatedModelInstance rest = CreateInstance();
        Assert.True(instance.Skeleton.TryGetClip("attack-melee-right", out Animation? attack));
        int[] attackNodes = instance.Skeleton.GetTracks(attack!).Select(t => t.NodeIndex).ToArray();
        Assert.NotEmpty(attackNodes);

        tracker.ApplyFrame(1, Frame("walk", PlayerClipMode.Loop), instance);
        instance.Update(0.35f);
        tracker.ApplyFrame(1, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 1), instance);
        instance.Update(0.2f);

        Assert.Equal("attack-melee-right", instance.CurrentClipName);
        Assert.Contains(attackNodes, n => instance.Pose.GetNodeLocalMatrix(n) != rest.Pose.GetNodeLocalMatrix(n));
    }

    [Fact]
    public void TheSwingStillPlaysOncePerSerial_AndStillReturnsToTheGait()
    {
        PlayerAnimationClipTracker tracker = new();
        AnimatedModelInstance instance = CreateInstance();

        tracker.ApplyFrame(1, Frame("walk", PlayerClipMode.Loop), instance);
        tracker.ApplyFrame(1, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 1), instance);
        instance.Update(0.1f);
        float time = instance.TimeSeconds;

        // The same serial must not restart the swing - and so must not re-reset the pose either.
        tracker.ApplyFrame(1, Frame("attack-melee-right", PlayerClipMode.OneShot, serial: 1), instance);
        Assert.Equal(time, instance.TimeSeconds);

        instance.Update(1f);
        Assert.True(instance.IsPlayingLoop("walk"));
    }
}
