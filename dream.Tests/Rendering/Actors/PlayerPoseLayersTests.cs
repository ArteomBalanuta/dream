using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Actors;

// The layers run over the real player model's skeleton and clips, with no GL.
public class PlayerPoseLayersTests
{
    private static AnimatedModelInstance CreateInstance(string clip)
    {
        ModelRoot model = ModelRoot.Load(PlayerModelAssetDefinition.ModelPath);
        Dictionary<string, Animation> clips = model.LogicalAnimations.ToDictionary(a => a.Name, a => a);
        AnimatedModelInstance instance = new(new AnimatedModelSkeleton(model, clips, skinJoints: null));
        instance.PlayLoop(clip);
        instance.Update(0.01f);
        return instance;
    }

    private static PlayerAnimationFrame Frame(float flinch = 0f, float tuck = 0f, float main = 0f, float off = 0f, float twist = 0f) =>
        new("idle", PlayerClipMode.Loop, 0, 1f, flinch, tuck, main, off, false, twist);

    private static Vector3 Down(Matrix4 arm) => Vector4.TransformRow(new Vector4(0f, -1f, 0f, 0f), arm).Xyz;

    [Fact]
    public void WithoutLayers_EveryOverrideIsIdentity()
    {
        AnimatedModelInstance instance = CreateInstance("idle");
        PlayerPoseLayers layers = new();

        layers.Update(instance, Frame());

        Assert.All(layers.Overrides.Values, matrix => Assert.Equal(Matrix4.Identity, matrix));
        Assert.True(layers.Overrides.ContainsKey(PlayerModelAssetDefinition.HeadNodeName));
    }

    [Fact]
    public void HoldingAnItem_RaisesTheArmForward()
    {
        AnimatedModelInstance instance = CreateInstance("idle");
        PlayerPoseLayers layers = new();

        layers.Update(instance, Frame());
        Assert.True(layers.TryGetArmMatrix(instance, offHand: false, out Matrix4 hanging));
        layers.Update(instance, Frame(main: 1f));
        Assert.True(layers.TryGetArmMatrix(instance, offHand: false, out Matrix4 raised));

        // The arm points down at rest; held, it points forward (+Z) and up.
        Assert.True(Down(hanging).Y < -0.9f);
        Assert.True(Down(raised).Z > 0.7f, $"held arm points {Down(raised)}");
        Assert.True(Down(raised).Y > -0.7f);
    }

    [Fact]
    public void HoldingAnItem_OverridesTheWalkSwing_SoTheItemDoesNotSwingWithIt()
    {
        AnimatedModelInstance instance = CreateInstance("walk");
        PlayerPoseLayers layers = new();
        List<Vector3> directions = [];

        for (int i = 0; i < 20; i++)
        {
            instance.Update(0.03f);
            layers.Update(instance, Frame(main: 1f));
            Assert.True(layers.TryGetArmMatrix(instance, offHand: false, out Matrix4 arm));
            directions.Add(Down(arm));
        }

        Assert.All(directions, d => Assert.True((d - directions[0]).Length < 0.01f));
    }

    [Fact]
    public void ArmWithoutAnItem_KeepsTheClipsSwing()
    {
        AnimatedModelInstance instance = CreateInstance("walk");
        PlayerPoseLayers layers = new();
        float minZ = float.MaxValue;
        float maxZ = float.MinValue;

        for (int i = 0; i < 20; i++)
        {
            instance.Update(0.03f);
            layers.Update(instance, Frame());
            Assert.True(layers.TryGetArmMatrix(instance, offHand: false, out Matrix4 arm));
            float z = Down(arm).Z;
            minZ = MathF.Min(minZ, z);
            maxZ = MathF.Max(maxZ, z);
        }

        Assert.True(maxZ - minZ > 0.5f, $"swing range {minZ}..{maxZ}");
    }

    [Fact]
    public void HitFlinch_TiltsTheTorsoBack_AndCarriesTheArmsAndHeadWithIt()
    {
        AnimatedModelInstance instance = CreateInstance("idle");
        PlayerPoseLayers layers = new();

        layers.Update(instance, Frame(flinch: 1f));

        Matrix4 torso = layers.Overrides[PlayerPoseLayers.TorsoNode];
        Vector3 top = Vector4.TransformRow(new Vector4(0f, 1f, 0f, 1f), torso).Xyz;
        Assert.True(top.Z < -0.05f, $"torso top moves back, got {top}");
        Assert.NotEqual(Matrix4.Identity, layers.Overrides[PlayerModelAssetDefinition.HeadNodeName]);
        Assert.NotEqual(Matrix4.Identity, layers.Overrides[PlayerPoseLayers.RightArmNode]);
        Assert.Equal(Matrix4.Identity, layers.Overrides[PlayerPoseLayers.LeftLegNode]);
    }

    [Fact]
    public void FlinchOnAParentedPart_EqualsTheTorsoTransformApplied()
    {
        // The head rides the torso: with the torso tilted, the head's world must be its animated
        // world with the same tilt inserted below the torso.
        AnimatedModelInstance instance = CreateInstance("idle");
        PlayerPoseLayers layers = new();
        layers.Update(instance, Frame(flinch: 1f));

        Vector3 headOrigin = Vector4.TransformRow(
            new Vector4(0f, 0f, 0f, 1f),
            layers.Overrides[PlayerModelAssetDefinition.HeadNodeName] * HeadWorld(instance)).Xyz;
        Vector3 restHead = Vector4.TransformRow(new Vector4(0f, 0f, 0f, 1f), HeadWorld(instance)).Xyz;

        Assert.True((headOrigin - restHead).Length > 0.05f);
        // The head stays attached: tilted about the hips, it only moves by the arc, not away.
        Assert.True((headOrigin - restHead).Length < 0.6f);
    }

    [Fact]
    public void AirTuck_SwingsTheLegsForwardAndBack()
    {
        AnimatedModelInstance instance = CreateInstance("idle");
        PlayerPoseLayers layers = new();

        layers.Update(instance, Frame(tuck: 1f));

        Vector3 leftFoot = Vector4.TransformRow(new Vector4(0f, -1f, 0f, 1f), layers.Overrides[PlayerPoseLayers.LeftLegNode]).Xyz;
        Vector3 rightFoot = Vector4.TransformRow(new Vector4(0f, -1f, 0f, 1f), layers.Overrides[PlayerPoseLayers.RightLegNode]).Xyz;
        Assert.True(leftFoot.Z > 0.3f);
        Assert.True(rightFoot.Z < -0.2f);
    }

    // The leg's model-space foot: override applied over the animated world, as the model draw does.
    private static Vector3 Foot(AnimatedModelInstance instance, PlayerPoseLayers layers, string node)
    {
        for (int i = 0; i < instance.Pose.RenderableNodeCount; i++)
        {
            if (instance.Pose.GetRenderableNode(i).Name == node)
            {
                Matrix4 world = instance.Pose.GetRenderableNodeWorldMatrix(i).ToOpenTK();
                return Vector4.TransformRow(new Vector4(0f, -0.3f, 0f, 1f), layers.Overrides[node] * world).Xyz;
            }
        }

        throw new InvalidOperationException(node);
    }

    [Fact]
    public void LegTwist_Zero_LeavesTheLegsExactlyAsTheClipDrawsThem()
    {
        AnimatedModelInstance instance = CreateInstance("walk");
        PlayerPoseLayers layers = new();

        layers.Update(instance, Frame(twist: 0f));

        Assert.Equal(Matrix4.Identity, layers.Overrides[PlayerPoseLayers.LeftLegNode]);
        Assert.Equal(Matrix4.Identity, layers.Overrides[PlayerPoseLayers.RightLegNode]);
    }

    [Theory]
    [InlineData(PlayerPoseLayers.LeftLegNode)]
    [InlineData(PlayerPoseLayers.RightLegNode)]
    public void LegTwist_YawsTheFootAboutTheModelsVerticalAxis(string leg)
    {
        AnimatedModelInstance instance = CreateInstance("walk");
        PlayerPoseLayers layers = new();
        instance.Update(0.11f);
        layers.Update(instance, Frame());
        Vector3 rest = Foot(instance, layers, leg);

        // The residual clamp: 45 degrees since the directional clips carry the rest (C2).
        float twist = PlayerAnimationStateMachine.MaxLegTwistRadians;
        layers.Update(instance, Frame(twist: twist));
        Vector3 twisted = Foot(instance, layers, leg);

        Vector3 expected = Vector4.TransformRow(new Vector4(rest, 1f), Matrix4.CreateRotationY(twist)).Xyz;
        Assert.True((twisted - expected).Length < 1e-3f, $"rest {rest} twisted {twisted} expected {expected}");
        Assert.Equal(rest.Y, twisted.Y, 3);
    }

    [Fact]
    public void LegTwist_PositiveTurnsTheStrideFromForwardTowardPlusX()
    {
        // Facing +Z and travelling +X is a raw +90 degrees, which C2 now spends on the strafe clip:
        // the residual the pose layers see is 0, so the clip alone turns the stride.
        float raw = PlayerAnimationStateMachine.LegTwist(0f, 1f, 1f, 0f);
        Assert.Equal(MathF.PI / 2f, raw, 4);
        Assert.Equal("walk-strafe-right", PlayerAnimationStateMachine.ResolveWalkClip(raw, out float residual));
        Assert.Equal(0f, residual, 5);

        // Within a sector the residual still turns the stride the same way, by its own angle.
        float twist = PlayerAnimationStateMachine.MaxLegTwistRadians;
        Vector3 plain = StrideTravel(0f);
        Vector3 twisted = StrideTravel(twist);
        Vector3 expected = Vector4.TransformRow(new Vector4(plain, 0f), Matrix4.CreateRotationY(twist)).Xyz;

        // Authored stride is along Z (X is only the hip offset's drift); the twist turns it toward X.
        Assert.True(MathF.Abs(plain.Z) > 0.05f && MathF.Abs(plain.X) < 0.005f, $"untwisted stride {plain}");
        Assert.True((twisted - expected).Length < 1e-3f, $"twisted stride {twisted}, expected {expected}");
        Assert.True(twisted.X * plain.Z > 0f, "a +Z stride turns toward +X for a positive twist");
    }

    // The guard in LegOverride is the OUTER one, MaxSprintLegTwistRadians, not the walk residual's
    // pi/4: the sprint and the attack-while-moving paths deliberately play one clip and still send
    // the raw delta, so clamping them here would leave a backwards sprint 135 degrees off travel.
    [Fact]
    public void LegTwist_HonoursASprintWidthTwist_WithoutClampingItToTheWalkResidual()
    {
        float sprint = PlayerAnimationStateMachine.MaxSprintLegTwistRadians;
        Assert.True(sprint > PlayerAnimationStateMachine.MaxLegTwistRadians);

        Vector3 plain = StrideTravel(0f);
        Vector3 wide = StrideTravel(sprint);
        Vector3 narrow = StrideTravel(PlayerAnimationStateMachine.MaxLegTwistRadians);
        Vector3 expected = Vector4.TransformRow(new Vector4(plain, 0f), Matrix4.CreateRotationY(sprint)).Xyz;

        Assert.True((wide - expected).Length < 1e-3f, $"sprint-width stride {wide}, expected {expected}");
        Assert.True((wide - narrow).Length > 1e-3f, "a pi/2 twist was clamped back to the pi/4 residual");
    }

    // The left foot's model-space displacement between two walk poses.
    private static Vector3 StrideTravel(float twist)
    {
        AnimatedModelInstance instance = CreateInstance("walk");
        PlayerPoseLayers layers = new();
        layers.Update(instance, Frame(twist: twist));
        Vector3 a = Foot(instance, layers, PlayerPoseLayers.LeftLegNode);
        instance.Update(0.15f);
        layers.Update(instance, Frame(twist: twist));
        Vector3 b = Foot(instance, layers, PlayerPoseLayers.LeftLegNode);
        return b - a;
    }

    [Fact]
    public void LegTwist_KeepsTheAirTuck()
    {
        AnimatedModelInstance instance = CreateInstance("idle");
        PlayerPoseLayers layers = new();

        float twist = PlayerAnimationStateMachine.MaxLegTwistRadians;
        layers.Update(instance, Frame(tuck: 1f));
        Vector3 tucked = Foot(instance, layers, PlayerPoseLayers.LeftLegNode);
        layers.Update(instance, Frame(tuck: 1f, twist: twist));
        Vector3 both = Foot(instance, layers, PlayerPoseLayers.LeftLegNode);
        Vector3 expected = Vector4.TransformRow(new Vector4(tucked, 1f), Matrix4.CreateRotationY(twist)).Xyz;

        Assert.True((both - expected).Length < 1e-3f, $"tucked {tucked} both {both} expected {expected}");
        Assert.NotEqual(Matrix4.Identity, layers.Overrides[PlayerPoseLayers.LeftLegNode]);
    }

    [Fact]
    public void LegTwist_DoesNotDisturbTheArmChainOrTheTorso()
    {
        AnimatedModelInstance instance = CreateInstance("walk");
        PlayerPoseLayers layers = new();
        layers.Update(instance, Frame(main: 1f, off: 1f));
        Assert.True(layers.TryGetArmMatrix(instance, offHand: false, out Matrix4 mainBefore));
        Assert.True(layers.TryGetArmMatrix(instance, offHand: true, out Matrix4 offBefore));

        layers.Update(instance, Frame(main: 1f, off: 1f, twist: 1.2f));
        Assert.True(layers.TryGetArmMatrix(instance, offHand: false, out Matrix4 mainAfter));
        Assert.True(layers.TryGetArmMatrix(instance, offHand: true, out Matrix4 offAfter));

        Assert.Equal(mainBefore, mainAfter);
        Assert.Equal(offBefore, offAfter);
        Assert.Equal(Matrix4.Identity, layers.Overrides[PlayerPoseLayers.TorsoNode]);
    }

    // The guard here is the OUTER one: a directional walk's residual is already under pi/4 when it
    // arrives, but the sprint and attack paths send the raw delta, so the pose layers clamp at
    // MaxSprintLegTwistRadians. Anything past that is clamped to it.
    [Fact]
    public void LegTwist_BeyondTheClamp_IsClamped()
    {
        AnimatedModelInstance instance = CreateInstance("walk");
        PlayerPoseLayers layers = new();

        layers.Update(instance, Frame(twist: PlayerAnimationStateMachine.MaxSprintLegTwistRadians));
        Matrix4 atClamp = layers.Overrides[PlayerPoseLayers.LeftLegNode];
        layers.Update(instance, Frame(twist: MathF.PI));

        Assert.Equal(atClamp, layers.Overrides[PlayerPoseLayers.LeftLegNode]);

        // And the narrower walk residual is NOT the clamp: it passes through as itself.
        layers.Update(instance, Frame(twist: PlayerAnimationStateMachine.MaxLegTwistRadians));
        Assert.NotEqual(atClamp, layers.Overrides[PlayerPoseLayers.LeftLegNode]);
    }

    [Fact]
    public void HitFlash_IsRedder_AndZeroWithoutAHit()
    {
        Vector3 none = PlayerPoseLayers.HitFlashOffset(0f);
        Vector3 full = PlayerPoseLayers.HitFlashOffset(1f);

        Assert.Equal(Vector3.Zero, none);
        Assert.True(full.X > 0f && full.Y < 0f && full.Z < 0f);
    }

    [Fact]
    public void TryGetArmMatrix_IsFalseBeforeTheFirstUpdate()
    {
        AnimatedModelInstance instance = CreateInstance("idle");

        Assert.False(new PlayerPoseLayers().TryGetArmMatrix(instance, offHand: false, out _));
    }

    private static Matrix4 NamedWorld(AnimatedModelInstance instance, string name)
    {
        for (int i = 0; i < instance.Pose.RenderableNodeCount; i++)
        {
            if (instance.Pose.GetRenderableNode(i).Name == name)
            {
                return instance.Pose.GetRenderableNodeWorldMatrix(i).ToOpenTK();
            }
        }

        throw new InvalidOperationException(name);
    }

    public static IEnumerable<object[]> BodyNodes() =>
    [
        [PlayerBodyNode.Head, PlayerModelAssetDefinition.HeadNodeName],
        [PlayerBodyNode.Torso, PlayerPoseLayers.TorsoNode],
        [PlayerBodyNode.LeftArm, PlayerPoseLayers.LeftArmNode],
        [PlayerBodyNode.RightArm, PlayerPoseLayers.RightArmNode],
        [PlayerBodyNode.LeftLeg, PlayerPoseLayers.LeftLegNode],
        [PlayerBodyNode.RightLeg, PlayerPoseLayers.RightLegNode]
    ];

    [Theory]
    [MemberData(nameof(BodyNodes))]
    public void TryGetNodeMatrix_IsTheOverrideComposedWorldMatrix_OfEachNode(PlayerBodyNode node, string name)
    {
        AnimatedModelInstance instance = CreateInstance("walk");
        PlayerPoseLayers layers = new();
        instance.Update(0.11f);
        layers.Update(instance, Frame(flinch: 1f, tuck: 1f, main: 1f, off: 1f, twist: 0.3f));

        Assert.True(layers.TryGetNodeMatrix(instance, node, out Matrix4 matrix));

        Matrix4 expected = layers.Overrides[name] * NamedWorld(instance, name);
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                Assert.Equal(expected[row, col], matrix[row, col], 4);
            }
        }
    }

    [Fact]
    public void TryGetNodeMatrix_IsFalseBeforeTheFirstUpdate_ForEveryNode()
    {
        AnimatedModelInstance instance = CreateInstance("idle");
        PlayerPoseLayers layers = new();

        foreach (PlayerBodyNode node in Enum.GetValues<PlayerBodyNode>())
        {
            Assert.False(layers.TryGetNodeMatrix(instance, node, out Matrix4 matrix));
            Assert.Equal(Matrix4.Identity, matrix);
        }
    }

    [Fact]
    public void TorsoNodeMatrix_CarriesTheFlinch_SoAWornCuirassTiltsWithTheBody()
    {
        AnimatedModelInstance instance = CreateInstance("idle");
        PlayerPoseLayers layers = new();

        layers.Update(instance, Frame());
        Assert.True(layers.TryGetNodeMatrix(instance, PlayerBodyNode.Torso, out Matrix4 rest));
        layers.Update(instance, Frame(flinch: 1f));
        Assert.True(layers.TryGetNodeMatrix(instance, PlayerBodyNode.Torso, out Matrix4 flinched));

        Vector3 restTop = Vector4.TransformRow(new Vector4(0f, 1f, 0f, 1f), rest).Xyz;
        Vector3 flinchedTop = Vector4.TransformRow(new Vector4(0f, 1f, 0f, 1f), flinched).Xyz;
        Assert.True(flinchedTop.Z < restTop.Z - 0.05f);
    }

    [Fact]
    public void TryGetArmMatrix_IsTheArmNodeMatrix()
    {
        AnimatedModelInstance instance = CreateInstance("walk");
        PlayerPoseLayers layers = new();
        layers.Update(instance, Frame(main: 1f, off: 0.5f));

        Assert.True(layers.TryGetArmMatrix(instance, offHand: false, out Matrix4 right));
        Assert.True(layers.TryGetNodeMatrix(instance, PlayerBodyNode.RightArm, out Matrix4 rightNode));
        Assert.True(layers.TryGetArmMatrix(instance, offHand: true, out Matrix4 left));
        Assert.True(layers.TryGetNodeMatrix(instance, PlayerBodyNode.LeftArm, out Matrix4 leftNode));
        Assert.Equal(rightNode, right);
        Assert.Equal(leftNode, left);
    }

    private static Matrix4 HeadWorld(AnimatedModelInstance instance)
    {
        for (int i = 0; i < instance.Pose.RenderableNodeCount; i++)
        {
            if (instance.Pose.GetRenderableNode(i).Name == PlayerModelAssetDefinition.HeadNodeName)
            {
                return instance.Pose.GetRenderableNodeWorldMatrix(i).ToOpenTK();
            }
        }

        throw new InvalidOperationException("no head node");
    }
}
