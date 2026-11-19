using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using Xunit;

namespace Dream.Tests.Rendering.Actors;

// The flame point is recomputed from the arm each frame: it must follow the arm through a swing and a raised
// hold, stay on the torch's Ember boxes, and vanish when the torch is unequipped.
public class HeldFlamePointPoseTests
{
    private const string Torch = "held.tool.torch";

    private static (AnimatedModelInstance Instance, PlayerPoseLayers Layers) Pose(
        string clip, float time, float mainHold, float offHold)
    {
        ModelRoot model = ModelRoot.Load(PlayerModelAssetDefinition.ModelPath);
        Dictionary<string, SharpGLTF.Schema2.Animation> clips = model.LogicalAnimations.ToDictionary(a => a.Name, a => a);
        AnimatedModelInstance instance = new(new AnimatedModelSkeleton(model, clips, skinJoints: null));
        instance.PlayLoop(clip);
        instance.Update(time);
        PlayerPoseLayers layers = new();
        layers.Update(instance, new PlayerAnimationFrame(clip, PlayerClipMode.Loop, 0, 1f, 0f, 0f, mainHold, offHold, false, 0f));
        return (instance, layers);
    }

    private static Vector3 Flame(AnimatedModelInstance instance, PlayerPoseLayers layers, PlayerEquipmentVisuals eq)
    {
        Assert.True(HeldWeaponRenderer.TryGetFlameModelPoint(instance, layers, in eq, out Vector3 p));
        return p;
    }

    [Theory]
    [InlineData("idle", 0.01f, 0f, 0f)]
    [InlineData("idle", 0.01f, 1f, 1f)]
    [InlineData(PlayerAnimationStateMachine.MeleeClip, 0.05f, 0f, 1f)]
    [InlineData(PlayerAnimationStateMachine.MeleeClip, 0.15f, 1f, 1f)]
    [InlineData(PlayerAnimationStateMachine.MeleeClip, 0.30f, 0f, 0f)]
    public void Flame_StaysInsideTheEmberBoxes_InAnyPose(string clip, float time, float mainHold, float offHold)
    {
        (AnimatedModelInstance instance, PlayerPoseLayers layers) = Pose(clip, time, mainHold, offHold);
        PlayerEquipmentVisuals eq = new(MainHand: null, OffHand: Torch);
        Assert.True(layers.TryGetArmMatrix(instance, offHand: true, out Matrix4 arm));
        WeaponModel model = WeaponMeshCatalog.Resolve(Torch);
        Matrix4 attach = Matrix4.CreateScale(HeldItemScale.Resolve(Torch)) *
            HeldWeaponRenderer.CreateGrip(model.Grip, offHand: true) * arm;
        List<Vector3> corners = [];
        foreach (WeaponPart part in model.Parts.Where(p => p.Material == WeaponMaterial.Ember))
        {
            WornArmourRenderer.AppendBoxCorners(part.ToMatrix() * attach, corners);
        }

        Vector3 min = corners.Aggregate(Vector3.ComponentMin);
        Vector3 max = corners.Aggregate(Vector3.ComponentMax);
        Vector3 f = Flame(instance, layers, eq);
        const float Eps = 1e-3f;
        Assert.True(
            f.X >= min.X - Eps && f.X <= max.X + Eps && f.Y >= min.Y - Eps && f.Y <= max.Y + Eps &&
            f.Z >= min.Z - Eps && f.Z <= max.Z + Eps,
            $"flame {f} outside ember bounds {min}..{max} ({clip} @ {time})");
    }

    [Fact]
    public void MainHandTorch_FollowsTheSwingingArm()
    {
        PlayerEquipmentVisuals eq = new(MainHand: Torch, OffHand: null);
        (AnimatedModelInstance i0, PlayerPoseLayers l0) = Pose("idle", 0.01f, 0f, 0f);
        (AnimatedModelInstance i1, PlayerPoseLayers l1) = Pose(PlayerAnimationStateMachine.MeleeClip, 0.15f, 0f, 0f);

        Vector3 idle = Flame(i0, l0, eq);
        Vector3 swing = Flame(i1, l1, eq);

        Assert.True((idle - swing).Length > 0.01f, $"flame did not move with the swing: {idle} vs {swing}");
    }

    [Fact]
    public void UnequippingTheTorch_ClearsThePoint()
    {
        (AnimatedModelInstance instance, PlayerPoseLayers layers) = Pose("idle", 0.01f, 0f, 1f);
        PlayerEquipmentVisuals lit = new(MainHand: null, OffHand: Torch);
        PlayerEquipmentVisuals none = PlayerEquipmentVisuals.Empty;

        Assert.True(HeldWeaponRenderer.TryGetFlameModelPoint(instance, layers, in lit, out _));
        Assert.False(HeldWeaponRenderer.TryGetFlameModelPoint(instance, layers, in none, out Vector3 p));
        Assert.Equal(Vector3.Zero, p);
    }

    // HeldLightAnchor.Body is what a frame with no recorded point falls back to: it has to sit near where the idle
    // off-hand torch head really is (same hand height, same side), lowered and raised hold alike, for any facing.
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void BodyAnchor_IsCloseToTheIdleOffHandFlame(float offHold)
    {
        (AnimatedModelInstance instance, PlayerPoseLayers layers) = Pose("idle", 0.01f, 0f, offHold);
        PlayerEquipmentVisuals eq = new(MainHand: null, OffHand: Torch);
        Vector3 flame = Flame(instance, layers, eq);
        Vector3 position = new(3f, 1f, -2f);
        foreach (Vector3 facing in new[] { Vector3.UnitX, -Vector3.UnitZ, new Vector3(1f, 0f, 1f), new Vector3(-2f, 0f, 1f) })
        {
            Vector3 world = Vector3.TransformPosition(flame, PlayerRenderState.CreateModelMatrix(position, facing));
            Vector3 body = HeldLightAnchor.Body(position, facing);
            Assert.True((world - body).Length < 0.2f, $"Body {body} is {(world - body).Length} m from the flame {world} ({facing})");
        }
    }
}
