using System.Numerics;
using SharpGLTF.Schema2;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using Dream.Simulation.Combat;

namespace Dream.Tests.Rendering.Actors;

// The shipped player model's "cleave" clip against the one MeleeArc timeline. gen_char_player.py repeats the tick
// numbers (its source gate forbids reading another file), so this is what keeps the clip's keys and length
// from drifting away from MeleeArcSwing: the clip is authored to the same wind-up, active window and recovery
// the server resolves hits on. It also checks the sweep itself - the sword goes left, forward, right.
public class PlayerCleaveClipTests
{
    private const float TimeTolerance = 1e-5f;

    private static readonly string[] AnimatedNodes = ["root", "torso", "head", "arm-left", "arm-right", "leg-left", "leg-right"];

    private static readonly Lazy<ModelRoot> Model = new(() => ModelRoot.Load(PlayerModelAssetDefinition.ModelPath));

    private static Animation Cleave() =>
        Model.Value.LogicalAnimations.Single(a => string.Equals(a.Name, PlayerAnimationStateMachine.CleaveClip, StringComparison.Ordinal));

    private static Node NodeNamed(string name) => Model.Value.LogicalNodes.Single(n => n.Name == name);

    private static (float Time, Quaternion Rotation)[] RotationKeys(string node) =>
        [.. Cleave().FindRotationChannel(NodeNamed(node)).GetRotationSampler().GetLinearKeys()];

    private static float[] KeyTimes(string node) => [.. RotationKeys(node).Select(k => k.Time)];

    private static Quaternion RotationAt(string node, float seconds) =>
        RotationKeys(node).Single(k => MathF.Abs(k.Time - seconds) < TimeTolerance).Rotation;

    // The sword arm's hanging axis in the model's frame at a key: where the hand and the weapon lie. +Z is forward,
    // +X the caster's LEFT (the model's right is -X).
    private static Vector3 SwordDirection(float seconds, bool mirror = false)
    {
        Quaternion torso = RotationAt("torso", seconds);
        Quaternion arm = RotationAt("arm-right", seconds);
        if (mirror)
        {
            // A reflection across the model's YZ plane: (x, y, z, w) -> (x, -y, -z, w).
            torso = new Quaternion(torso.X, -torso.Y, -torso.Z, torso.W);
            arm = new Quaternion(arm.X, -arm.Y, -arm.Z, arm.W);
        }

        return Vector3.Normalize(Vector3.Transform(Vector3.Transform(-Vector3.UnitY, arm), torso));
    }

    private const float ActiveMidSeconds = MeleeArcSwing.WindupSeconds + (MeleeArcSwing.ActiveSeconds / 2f);

    // True when the sword is left at the end of the wind-up, forward at the active midpoint and right at its end.
    private static bool SweepsLeftToRight(bool mirror)
    {
        Vector3 windup = SwordDirection(MeleeArcSwing.WindupSeconds, mirror);
        Vector3 mid = SwordDirection(ActiveMidSeconds, mirror);
        Vector3 end = SwordDirection(MeleeArcSwing.ActiveEndSeconds, mirror);
        return windup.X > 0.5f && mid.Z > 0.7f && MathF.Abs(mid.X) < 0.35f && end.X < -0.5f &&
            windup.X > mid.X && mid.X > end.X;
    }

    [Fact]
    public void TheShippedModel_HasACleaveClip_AsLongAsTheSwing()
    {
        Assert.Equal(MeleeArcSwing.TotalSeconds, Cleave().Duration, 4);
        Assert.Equal(PlayerAnimationStateMachine.CleaveSeconds, Cleave().Duration, 4);
    }

    [Fact]
    public void EveryClipTheStateMachineNamesIsStillOnTheModel()
    {
        string[] names = [.. Model.Value.LogicalAnimations.Select(a => a.Name)];
        foreach (string clip in new[]
        {
            PlayerAnimationStateMachine.IdleClip, PlayerAnimationStateMachine.WalkClip, PlayerAnimationStateMachine.RunClip,
            PlayerAnimationStateMachine.SprintClip, PlayerAnimationStateMachine.DieClip, PlayerAnimationStateMachine.MeleeClip,
            PlayerAnimationStateMachine.ShootClip, PlayerAnimationStateMachine.WalkBackClip,
            PlayerAnimationStateMachine.StrafeLeftClip, PlayerAnimationStateMachine.StrafeRightClip,
            PlayerAnimationStateMachine.CleaveClip
        })
        {
            Assert.Contains(clip, names);
        }
    }

    [Theory]
    [InlineData("arm-right")]
    [InlineData("torso")]
    [InlineData("leg-left")]
    public void TheClipHasAKeyAtEveryPhaseBorder(string node)
    {
        float[] times = KeyTimes(node);

        Assert.True(MathF.Abs(times[0]) < TimeTolerance, "first key at 0");
        foreach (float border in new[]
        {
            MeleeArcSwing.WindupSeconds, ActiveMidSeconds, MeleeArcSwing.ActiveEndSeconds, MeleeArcSwing.TotalSeconds
        })
        {
            Assert.Contains(times, t => MathF.Abs(t - border) < TimeTolerance);
        }

        Assert.True(MathF.Abs(times[^1] - MeleeArcSwing.TotalSeconds) < TimeTolerance, "last key at the total");
        Assert.True(times.Length >= 6, "extra keys shape the easing");
    }

    [Fact]
    public void TheClipStartsAndEndsOnTheRestPose()
    {
        foreach (string node in AnimatedNodes.Where(n => n != "root"))
        {
            (float Time, Quaternion Rotation)[] keys = RotationKeys(node);
            Assert.True(Quaternion.Dot(keys[0].Rotation, Quaternion.Identity) > 0.99999f, $"{node} first key");
            Assert.True(Quaternion.Dot(keys[^1].Rotation, Quaternion.Identity) > 0.99999f, $"{node} last key");
        }

        var root = Cleave().FindTranslationChannel(NodeNamed("root")).GetTranslationSampler().GetLinearKeys().ToArray();
        Assert.True(root[0].Value.Length() < 1e-6f);
        Assert.True(root[^1].Value.Length() < 1e-6f);
    }

    [Fact]
    public void TheRootDipsInTheWindup()
    {
        var root = Cleave().FindTranslationChannel(NodeNamed("root")).GetTranslationSampler().GetLinearKeys().ToArray();

        float windupDip = root.Single(k => MathF.Abs(k.Key - MeleeArcSwing.WindupSeconds) < TimeTolerance).Value.Y;

        // 0.06..0.10 m at 1.9 m = 2.7 units: about 1.42 units per m.
        Assert.InRange(-windupDip / 1.42f, 0.05f, 0.11f);
    }

    [Fact]
    public void TheSwordSweepsLeftToForwardToRight_AcrossTheActiveWindow()
    {
        Assert.True(SwordDirection(MeleeArcSwing.WindupSeconds).X > 0.5f, "drawn across the body to the left");
        Vector3 mid = SwordDirection(ActiveMidSeconds);
        Assert.True(mid.Z > 0.7f && MathF.Abs(mid.X) < 0.35f, "forward at the midpoint of the window");
        Assert.True(SwordDirection(MeleeArcSwing.ActiveEndSeconds).X < -0.5f, "finished on the right");
        Assert.True(SweepsLeftToRight(mirror: false));
    }

    // A test that cannot fail is not a test: the mirrored clip sweeps right to left and must be refused.
    [Fact]
    public void TheSweepGate_RejectsAMirroredClip()
    {
        Assert.False(SweepsLeftToRight(mirror: true));
    }
}
