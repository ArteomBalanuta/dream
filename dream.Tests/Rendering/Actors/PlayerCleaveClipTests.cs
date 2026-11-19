using System.Numerics;
using SharpGLTF.Schema2;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Effects;
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

    // Where the blade tip is at a key, in metres, in the model's frame (+Z forward, +X the caster's LEFT, Y up from the
    // feet): root dip -> torso node -> arm-right node -> the fist (HeldWeaponRenderer's grip) -> the blade, which
    // CreateGrip lays along the arm's +Z, BladeReachMetres long. This measures the BLADE, not the arm's hanging axis:
    // a raised arm lifts it, which is how the old clip kept a vertical sword through the whole sweep.
    private static Vector3 BladeTip(float seconds, bool mirror = false)
    {
        Quaternion torso = RotationAt("torso", seconds);
        Quaternion arm = RotationAt("arm-right", seconds);
        Vector3 dip = Cleave().FindTranslationChannel(NodeNamed("root")).GetTranslationSampler().GetLinearKeys()
            .Single(k => MathF.Abs(k.Key - seconds) < TimeTolerance).Value;
        if (mirror)
        {
            // A reflection across the model's YZ plane: (x, y, z, w) -> (x, -y, -z, w).
            torso = new Quaternion(torso.X, -torso.Y, -torso.Z, torso.W);
            arm = new Quaternion(arm.X, -arm.Y, -arm.Z, arm.W);
        }

        Vector3 armNode = NodeNamed("arm-right").LocalTransform.Translation;
        Vector3 torsoNode = NodeNamed("torso").LocalTransform.Translation;
        var fist = new Vector3(-HeldWeaponRenderer.FistOffsetX, HeldWeaponRenderer.FistOffsetY, 0f);
        Vector3 blade = Vector3.UnitZ * (FxBladeTrailGeometry.BladeReachMetres * HeldItemScale.ModelUnitsPerMetre);
        Vector3 inTorso = armNode + Vector3.Transform(fist + blade, arm);
        Vector3 inRoot = torsoNode + Vector3.Transform(inTorso, torso);
        return (dip + inRoot) / HeldItemScale.ModelUnitsPerMetre;
    }

    // The effect convention: 0 is the aim (+Z), negative is the caster's left (+X).
    private static float TipAngle(Vector3 tip) => -MathF.Atan2(tip.X, tip.Z);

    private static float TipRadius(Vector3 tip) => MathF.Sqrt((tip.X * tip.X) + (tip.Z * tip.Z));

    private const float ActiveMidSeconds = MeleeArcSwing.WindupSeconds + (MeleeArcSwing.ActiveSeconds / 2f);

    private const float AngleToleranceRadians = 10f * MathF.PI / 180f;

    private static float SecondsAt(float progress) => MeleeArcSwing.WindupSeconds + (progress * MeleeArcSwing.ActiveSeconds);

    // Why the tip is off the path at `progress`, or null when it is on it: angle within 10 degrees, radius within
    // 0.15 m, height within 0.12 m of the FxBladeTrailGeometry path the trail is laid on.
    private static string? OffPath(float progress, bool mirror)
    {
        Vector3 tip = BladeTip(SecondsAt(progress), mirror);
        float angle = TipAngle(tip);
        float wantAngle = FxBladeTrailGeometry.TipAngleRadians(progress);
        if (MathF.Abs(angle - wantAngle) > AngleToleranceRadians)
        {
            return $"p={progress:F2}: angle {angle * 180f / MathF.PI:F1} deg, want {wantAngle * 180f / MathF.PI:F1}";
        }

        if (MathF.Abs(TipRadius(tip) - FxBladeTrailGeometry.TipRadiusMetres) > 0.15f)
        {
            return $"p={progress:F2}: radius {TipRadius(tip):F2} m";
        }

        float wantHeight = FxBladeTrailGeometry.TipHeightMetres(progress);
        if (MathF.Abs(tip.Y - wantHeight) > 0.12f)
        {
            return $"p={progress:F2}: height {tip.Y:F2} m, want {wantHeight:F2}";
        }

        return null;
    }

    private static string? OffPathAnywhere(bool mirror) =>
        new[] { 0f, 0.5f, 1f }.Select(p => OffPath(p, mirror)).FirstOrDefault(m => m is not null);

    // Constant angular speed: at the window's thirds the tip is a third and two thirds of the way across.
    private static string? NotConstantSpeed(bool mirror)
    {
        foreach (float p in new[] { 1f / 3f, 2f / 3f })
        {
            float angle = TipAngle(BladeTip(SecondsAt(p), mirror));
            if (MathF.Abs(angle - FxBladeTrailGeometry.TipAngleRadians(p)) > AngleToleranceRadians)
            {
                return $"p={p:F2}: angle {angle * 180f / MathF.PI:F1} deg";
            }
        }

        return null;
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
    public void TheBladeTip_RunsAlongTheTrailPath_AtTheWindowStartMidAndEnd()
    {
        Assert.Null(OffPathAnywhere(mirror: false));
    }

    // The thirds of a 6-tick window are ticks 10 and 12, which are keys; a different window needs different keys.
    [Fact]
    public void TheBladeTip_SweepsAtConstantSpeed_AcrossTheActiveWindow()
    {
        Assert.Equal(6, MeleeArcSwing.ActiveTicks);
        Assert.Null(NotConstantSpeed(mirror: false));
    }

    // A test that cannot fail is not a test: the mirrored clip sweeps right to left and must be refused.
    [Fact]
    public void TheTipGate_RejectsAMirroredClip()
    {
        Assert.NotNull(OffPathAnywhere(mirror: true));
        Assert.NotNull(NotConstantSpeed(mirror: true));
    }
}
