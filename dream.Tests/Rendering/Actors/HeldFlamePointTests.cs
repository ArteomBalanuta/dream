using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using Xunit;

namespace Dream.Tests.Rendering.Actors;

// The held light must burn where the torch in the animated hand is drawn. The torch is carried in the figure's
// off (left) hand, so its flame is on the figure's left, not at the fixed right-hand anchor.
public class HeldFlamePointTests
{
    private const string Torch = "held.tool.torch";
    private const string Blade = "held.tool.blade";

    private static (AnimatedModelInstance Instance, PlayerPoseLayers Layers) Pose()
    {
        ModelRoot model = ModelRoot.Load(PlayerModelAssetDefinition.ModelPath);
        Dictionary<string, SharpGLTF.Schema2.Animation> clips = model.LogicalAnimations.ToDictionary(a => a.Name, a => a);
        AnimatedModelInstance instance = new(new AnimatedModelSkeleton(model, clips, skinJoints: null));
        instance.PlayLoop("idle");
        instance.Update(0.01f);
        PlayerPoseLayers layers = new();
        layers.Update(instance, new PlayerAnimationFrame("idle", PlayerClipMode.Loop, 0, 1f, 0f, 0f, 0f, 0f, false, 0f));
        return (instance, layers);
    }

    private static Vector3 FlameWorld(PlayerEquipmentVisuals equipment, Matrix4 modelMatrix, out bool found)
    {
        (AnimatedModelInstance instance, PlayerPoseLayers layers) = Pose();
        found = HeldWeaponRenderer.TryGetFlameModelPoint(instance, layers, in equipment, out Vector3 point);
        return Vector3.TransformPosition(point, modelMatrix);
    }

    [Fact]
    public void TorchInTheOffHand_FlameLiesInsideTheDrawnEmberBoxes()
    {
        PlayerEquipmentVisuals equipment = new(MainHand: null, OffHand: Torch);
        Matrix4 modelMatrix = PlayerRenderState.CreateModelMatrix(new Vector3(3f, 0f, -2f), new Vector3(1f, 0f, 1f));
        (AnimatedModelInstance instance, PlayerPoseLayers layers) = Pose();
        Assert.True(layers.TryGetArmMatrix(instance, offHand: true, out Matrix4 arm));
        WeaponModel model = WeaponMeshCatalog.Resolve(Torch);
        Matrix4 attach = Matrix4.CreateScale(HeldItemScale.Resolve(Torch)) *
            HeldWeaponRenderer.CreateGrip(model.Grip, offHand: true) * arm * modelMatrix;

        List<Vector3> corners = [];
        foreach (WeaponPart part in model.Parts.Where(p => p.Material == WeaponMaterial.Ember))
        {
            WornArmourRenderer.AppendBoxCorners(part.ToMatrix() * attach, corners);
        }

        Assert.NotEmpty(corners);
        Vector3 min = corners.Aggregate(Vector3.ComponentMin);
        Vector3 max = corners.Aggregate(Vector3.ComponentMax);
        Vector3 flame = FlameWorld(equipment, modelMatrix, out bool found);

        Assert.True(found);
        const float Eps = 1e-3f;
        Assert.True(
            flame.X >= min.X - Eps && flame.X <= max.X + Eps &&
            flame.Y >= min.Y - Eps && flame.Y <= max.Y + Eps &&
            flame.Z >= min.Z - Eps && flame.Z <= max.Z + Eps,
            $"flame {flame} outside the ember bounds {min}..{max}");
    }

    [Theory]
    [InlineData(0f, 0f, 1f)]
    [InlineData(1f, 0f, 0f)]
    [InlineData(-1f, 0f, -1f)]
    [InlineData(0.3f, 0f, -1f)]
    public void TorchInTheOffHand_BurnsOnTheFiguresLeft(float fx, float fy, float fz)
    {
        Vector3 facing = new Vector3(fx, fy, fz).Normalized();
        Vector3 position = new(5f, 0f, 5f);
        Vector3 flame = FlameWorld(
            new PlayerEquipmentVisuals(MainHand: null, OffHand: Torch),
            PlayerRenderState.CreateModelMatrix(position, facing),
            out bool found);

        Assert.True(found);
        Vector3 figureRight = new(-facing.Z, 0f, facing.X);
        Assert.True(Vector3.Dot(flame - position, figureRight) < 0f, $"flame {flame} is not on the figure's left (facing {facing})");
    }

    [Fact]
    public void TorchInTheMainHand_BurnsOnTheFiguresRight()
    {
        Vector3 facing = Vector3.UnitZ;
        Vector3 position = Vector3.Zero;
        Vector3 flame = FlameWorld(
            new PlayerEquipmentVisuals(MainHand: Torch, OffHand: null),
            PlayerRenderState.CreateModelMatrix(position, facing),
            out bool found);

        Assert.True(found);
        Vector3 figureRight = new(-facing.Z, 0f, facing.X);
        Assert.True(Vector3.Dot(flame - position, figureRight) > 0f, $"flame {flame} is not on the figure's right");
    }

    [Fact]
    public void TorchInTheOffHandWithABladeInTheMain_ChoosesTheOffHand()
    {
        Matrix4 modelMatrix = PlayerRenderState.CreateModelMatrix(Vector3.Zero, Vector3.UnitZ);
        Vector3 both = FlameWorld(new PlayerEquipmentVisuals(MainHand: Blade, OffHand: Torch), modelMatrix, out bool foundBoth);
        Vector3 offOnly = FlameWorld(new PlayerEquipmentVisuals(MainHand: null, OffHand: Torch), modelMatrix, out bool foundOff);

        Assert.True(foundBoth);
        Assert.True(foundOff);
        Assert.Equal(offOnly, both);
    }

    [Fact]
    public void TorchInBothHands_ChoosesTheOffHandLikeTheLight()
    {
        Matrix4 modelMatrix = PlayerRenderState.CreateModelMatrix(Vector3.Zero, Vector3.UnitZ);
        Vector3 both = FlameWorld(new PlayerEquipmentVisuals(MainHand: Torch, OffHand: Torch), modelMatrix, out _);
        Vector3 offOnly = FlameWorld(new PlayerEquipmentVisuals(MainHand: null, OffHand: Torch), modelMatrix, out _);

        Assert.Equal(offOnly, both);
    }

    [Fact]
    public void NothingOrNonLightItems_HaveNoFlame()
    {
        Matrix4 modelMatrix = Matrix4.Identity;
        FlameWorld(PlayerEquipmentVisuals.Empty, modelMatrix, out bool empty);
        FlameWorld(new PlayerEquipmentVisuals(MainHand: Blade, OffHand: "held.shield.kite"), modelMatrix, out bool nonLight);

        Assert.False(empty);
        Assert.False(nonLight);
    }
}
