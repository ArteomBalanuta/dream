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

    private static PlayerAnimationFrame Frame(float flinch = 0f, float tuck = 0f, float main = 0f, float off = 0f) =>
        new("idle", PlayerClipMode.Loop, 0, 1f, flinch, tuck, main, off, false);

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
