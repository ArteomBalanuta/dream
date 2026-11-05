using Dream.Client.Scenes;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using Xunit;

namespace Dream.Tests.Client.Scenes;

// The capture's actorbox must cover what is drawn: the authored model bounds AND every equipped worn or
// held box. The A3 contrast gate crops to it, so a box that clips the armour measures the wrong pixels.
public class PlaySceneCaptureActorBoxTests
{
    private static readonly Vector2i Viewport = new(1280, 720);

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

    // A camera looking at the origin from the side the figure faces (+Z), isometric-ish and above.
    private static Matrix4 ViewProjection() =>
        Matrix4.LookAt(new Vector3(6f, 7f, 6f), new Vector3(0f, 0.9f, 0f), Vector3.UnitY) *
        Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(40f), 16f / 9f, 0.1f, 100f);

    private static (float X0, float Y0, float X1, float Y1) BoxFor(PlayerEquipmentVisuals equipment, out List<Vector3> equipmentCorners)
    {
        (AnimatedModelInstance instance, PlayerPoseLayers layers) = Pose();
        Matrix4 modelMatrix = Matrix4.CreateScale(PlayerModelAssetDefinition.VisualScale);
        equipmentCorners = [];
        WornArmourRenderer.AppendCorners(instance, layers, in equipment, modelMatrix, equipmentCorners);
        HeldWeaponRenderer.AppendCorners(instance, layers, in equipment, modelMatrix, equipmentCorners);

        List<Vector3> points = PlayScene.BuildActorPoints(
            Vector3.Zero, 0f, PlayerModelAssetDefinition.Normalization.ResolveScaledDimensions(), equipmentCorners, Vector3.Zero);
        PlayScene.ProjectBounds(points, ViewProjection(), Viewport, out float x0, out float y0, out float x1, out float y1, out _);
        return (x0, y0, x1, y1);
    }

    [Fact]
    public void Unequipped_AddsNothingToTheAuthoredBox()
    {
        (float x0, float y0, float x1, float y1) = BoxFor(PlayerEquipmentVisuals.Empty, out List<Vector3> corners);

        Assert.Empty(corners);
        List<Vector3> authoredOnly = PlayScene.BuildActorPoints(
            Vector3.Zero, 0f, PlayerModelAssetDefinition.Normalization.ResolveScaledDimensions(), [], Vector3.Zero);
        Assert.Equal(8, authoredOnly.Count);
        PlayScene.ProjectBounds(authoredOnly, ViewProjection(), Viewport, out float a0, out float b0, out float a1, out float b1, out _);
        Assert.Equal((a0, b0, a1, b1), (x0, y0, x1, y1));
    }

    [Theory]
    [InlineData("worn.back.grave-mantle")]
    [InlineData("worn.back.hooded-cloak")]
    public void WornBack_IsInsideTheBox_AndWidensItPastTheAuthoredBounds(string visualId)
    {
        PlayerEquipmentVisuals equipment = new() { Back = visualId };
        Assert.True(ArmourMeshCatalog.IsKnown(visualId));

        (float x0, float y0, float x1, float y1) = BoxFor(equipment, out List<Vector3> corners);
        (float ax0, float ay0, float ax1, float ay1) = BoxFor(PlayerEquipmentVisuals.Empty, out _);

        Assert.NotEmpty(corners);
        Matrix4 viewProjection = ViewProjection();
        foreach (Vector3 corner in corners)
        {
            Vector4 clip = Vector4.TransformRow(new Vector4(corner, 1f), viewProjection);
            float sx = (clip.X / clip.W + 1f) * 0.5f * Viewport.X;
            float sy = (1f - clip.Y / clip.W) * 0.5f * Viewport.Y;
            Assert.InRange(sx, x0 - 0.01f, x1 + 0.01f);
            Assert.InRange(sy, y0 - 0.01f, y1 + 0.01f);
        }

        // Never smaller than the authored box, and larger on at least one side for a piece that sticks out.
        Assert.True(x0 <= ax0 && y0 <= ay0 && x1 >= ax1 && y1 >= ay1);
        Assert.True(x0 < ax0 || y0 < ay0 || x1 > ax1 || y1 > ay1, "the equipped back piece did not widen the box");
    }

    [Fact]
    public void GraveMantle_ReachesItsCatalogExtents_InModelSpace()
    {
        (AnimatedModelInstance instance, PlayerPoseLayers layers) = Pose();
        PlayerEquipmentVisuals equipment = new() { Back = "worn.back.grave-mantle" };
        List<Vector3> corners = [];

        WornArmourRenderer.AppendCorners(instance, layers, in equipment, Matrix4.Identity, corners);

        Assert.Equal(ArmourMeshCatalog.Resolve("worn.back.grave-mantle")!.Parts.Count * 8, corners.Count);
        Assert.True(corners.Max(c => c.X) >= 0.4f);
        Assert.True(corners.Min(c => c.X) <= -0.4f);
    }

    [Fact]
    public void EquipmentMovedSinceTheLastDraw_MovesWithTheFeet()
    {
        List<Vector3> equipment = [new Vector3(1f, 2f, 3f)];

        List<Vector3> points = PlayScene.BuildActorPoints(
            new Vector3(10f, 0f, 0f), 0f, PlayerModelAssetDefinition.Normalization.ResolveScaledDimensions(), equipment, new Vector3(4f, 0f, 0f));

        Assert.Equal(new Vector3(7f, 2f, 3f), points[^1]);
    }
}
