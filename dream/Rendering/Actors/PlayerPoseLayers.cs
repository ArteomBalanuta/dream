using OpenTK.Mathematics;
using Dream.Rendering.Models;

namespace Dream.Rendering.Actors;

// The procedural layers over one player's clip, as per-node overrides for the model draw: the
// hit flinch (the torso, and the parts it carries, pitch back), the jump tuck (legs), and the raised
// arms that hold an item. An override O is applied as O * nodeWorld, so the layers are exact: a
// part that rides the torso takes the torso's flinch, and a held arm keeps the clip's own shoulder.
//
// One instance per player. Update allocates nothing after the first call, and the dictionary it
// fills is the one the model draw reads.
public sealed class PlayerPoseLayers
{
    public const string TorsoNode = "torso";
    public const string LeftArmNode = "arm-left";
    public const string RightArmNode = "arm-right";
    public const string LeftLegNode = "leg-left";
    public const string RightLegNode = "leg-right";

    // Raised-arm angles about the shoulder (negative swings the arm forward), radians.
    public const float MainArmHoldRadians = -0.96f;
    public const float OffArmHoldRadians = -0.62f;

    // Hips, in the torso's own frame: the flinch pivots here.
    private static readonly Vector3 TorsoPivot = new(0f, 0.3f, 0f);
    private const float FlinchRadians = -0.34f;
    private const float TuckForwardRadians = -0.75f;
    private const float TuckBackRadians = 0.45f;

    private readonly Dictionary<string, Matrix4> _overrides = new(StringComparer.Ordinal);
    private bool _resolved;
    private int _leftArm = -1;
    private int _rightArm = -1;
    private int _head = -1;
    private int _leftLeg = -1;
    private int _rightLeg = -1;

    public IReadOnlyDictionary<string, Matrix4> Overrides => _overrides;

    // The red flash of a hit, as the actor shader tint offset (base colour * (1 + offset)).
    public static Vector3 HitFlashOffset(float flinch) => new Vector3(0.9f, -0.55f, -0.55f) * Math.Clamp(flinch, 0f, 1f);

    public void Update(AnimatedModelInstance instance, in PlayerAnimationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(instance);
        AnimatedModelPose pose = instance.Pose;
        Resolve(pose);

        Matrix4 flinch = Matrix4.Identity;
        if (frame.HitFlinch > 0.001f)
        {
            flinch = Matrix4.CreateTranslation(-TorsoPivot) *
                Matrix4.CreateRotationX(FlinchRadians * frame.HitFlinch) *
                Matrix4.CreateTranslation(TorsoPivot);
        }

        _overrides[PlayerModelAssetDefinition.HeadNodeName] = CarriedOverride(pose, _head, flinch, 0f, 0f);
        _overrides[TorsoNode] = flinch;
        _overrides[LeftArmNode] = CarriedOverride(pose, _leftArm, flinch, frame.OffArmHold, OffArmHoldRadians);
        _overrides[RightArmNode] = CarriedOverride(pose, _rightArm, flinch, frame.MainArmHold, MainArmHoldRadians);

        // Legs: the air tuck, then the travel twist. The twist is a yaw about the model's central
        // vertical axis (the hip line), in model space after the clip's own leg pose, so it turns the
        // whole stride toward the travel direction. As O * W (W the leg's animated world), that is
        // O = Tuck * W * RotY(twist) * W^-1; with no twist this is exactly the plain tuck.
        // With the directional clips (C2) the clip already carries the 90 and 180 degree cases, so
        // frame.LegTwistRadians is the within-sector residual and the clamp below is only a guard.
        Matrix4 tuckLeft = frame.AirTuck > 0.001f ? Matrix4.CreateRotationX(TuckForwardRadians * frame.AirTuck) : Matrix4.Identity;
        Matrix4 tuckRight = frame.AirTuck > 0.001f ? Matrix4.CreateRotationX(TuckBackRadians * frame.AirTuck) : Matrix4.Identity;
        _overrides[LeftLegNode] = LegOverride(pose, _leftLeg, tuckLeft, frame.LegTwistRadians);
        _overrides[RightLegNode] = LegOverride(pose, _rightLeg, tuckRight, frame.LegTwistRadians);
    }

    private static Matrix4 LegOverride(AnimatedModelPose pose, int node, Matrix4 tuck, float twistRadians)
    {
        // The OUTER guard, so it is the widest twist any frame can carry: the state machine has
        // already narrowed a directional walk's residual to MaxLegTwistRadians, while the paths that
        // deliberately play one clip (sprint, an attack while moving, the airborne idle) still send
        // the raw delta under MaxSprintLegTwistRadians. Clamping those to pi/4 here would leave a
        // backwards sprint's stride 135 degrees off its travel.
        twistRadians = Math.Clamp(twistRadians, -PlayerAnimationStateMachine.MaxSprintLegTwistRadians, PlayerAnimationStateMachine.MaxSprintLegTwistRadians);
        if (node < 0 || MathF.Abs(twistRadians) < 1e-4f)
        {
            return tuck;
        }

        Matrix4 world = pose.GetNodeWorldMatrix(node).ToOpenTK();
        if (MathF.Abs(world.Determinant) < 1e-9f)
        {
            return tuck;
        }

        return tuck * world * Matrix4.CreateRotationY(twistRadians) * Matrix4.Invert(world);
    }

    // The world matrix, in model space, of the hand's arm with its override applied: what a held
    // item rides on. False before the first Update or when the model has no such arm.
    public bool TryGetArmMatrix(AnimatedModelInstance instance, bool offHand, out Matrix4 arm)
    {
        int index = offHand ? _leftArm : _rightArm;
        if (!_resolved || index < 0)
        {
            arm = Matrix4.Identity;
            return false;
        }

        arm = instance.Pose.GetNodeWorldMatrix(index).ToOpenTK();
        if (_overrides.TryGetValue(offHand ? LeftArmNode : RightArmNode, out Matrix4 nodeOverride))
        {
            arm = nodeOverride * arm;
        }

        return true;
    }

    // Override for a node that rides the torso (arm or head): the torso's flinch, and for an arm
    // the hold. With L the node's animated local matrix and L' the one with the held arm angle,
    // O = L' * F * L^-1 gives O * (L * torsoWorld) = L' * F * torsoWorld.
    private static Matrix4 CarriedOverride(AnimatedModelPose pose, int node, Matrix4 flinch, float hold, float holdRadians)
    {
        if (node < 0 || (hold <= 0.001f && flinch == Matrix4.Identity))
        {
            return Matrix4.Identity;
        }

        Matrix4 local = pose.GetNodeLocalMatrix(node).ToOpenTK();
        Matrix4 held = local;
        if (hold > 0.001f)
        {
            float animated = MathF.Atan2(local.M23, local.M22);
            float angle = animated + ((holdRadians - animated) * Math.Min(hold, 1f));
            held = Matrix4.CreateRotationX(angle) * Matrix4.CreateTranslation(local.Row3.Xyz);
        }

        if (MathF.Abs(local.Determinant) < 1e-9f)
        {
            return Matrix4.Identity;
        }

        return held * flinch * Matrix4.Invert(local);
    }

    private void Resolve(AnimatedModelPose pose)
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;
        for (int i = 0; i < pose.RenderableNodeCount; i++)
        {
            string? name = pose.GetRenderableNode(i).Name;
            int logical = pose.Skeleton.GetRenderableNodeLogicalIndex(i);
            switch (name)
            {
                case LeftArmNode: _leftArm = logical; break;
                case RightArmNode: _rightArm = logical; break;
                case LeftLegNode: _leftLeg = logical; break;
                case RightLegNode: _rightLeg = logical; break;
                case PlayerModelAssetDefinition.HeadNodeName: _head = logical; break;
            }
        }
    }
}
