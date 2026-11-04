using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using Dream.Simulation.Enemies;

namespace Dream.Tests.Rendering.Actors;


public class EnemyRenderStateTests
{
    [Fact]
    public void CreateWolf_Builds_Model_Matrix_From_Position_And_Forward()
    {
        EnemyRenderState state = EnemyRenderState.CreateWolf(
            new Vector3(4f, 1f, 5f),
            Vector3.UnitX,
            EnemyBehaviorState.Chasing,
            scale: 1.5f);

        Assert.Equal(WolfAssetDefinition.MoveClip, state.ClipName);
        Assert.Equal("enemy", state.ShaderName);
        Assert.Equal(4f, state.ModelMatrix.M41, 3);
        Assert.Equal(1f, state.ModelMatrix.M42, 3);
        Assert.Equal(5f, state.ModelMatrix.M43, 3);
    }

    [Fact]
    public void CreateWolf_Uses_Normalized_Default_Scale()
    {
        EnemyRenderState state = EnemyRenderState.CreateWolf(
            new Vector3(0f, 0f, 0f),
            -Vector3.UnitZ,
            EnemyBehaviorState.Idle);

        float expectedScale = WolfAssetDefinition.Normalization.ResolveUniformScale();

        Assert.Equal(expectedScale, new Vector3(state.ModelMatrix.M11, state.ModelMatrix.M12, state.ModelMatrix.M13).Length, 3);
        Assert.Equal(expectedScale, new Vector3(state.ModelMatrix.M21, state.ModelMatrix.M22, state.ModelMatrix.M23).Length, 3);
        Assert.Equal(expectedScale, new Vector3(state.ModelMatrix.M31, state.ModelMatrix.M32, state.ModelMatrix.M33).Length, 3);
    }

    [Fact]
    public void DefaultWolfVisual_RendersExactlyAsBefore()
    {
        EnemyVisual visual = EnemyVisualCatalog.Resolve(null);
        Vector3 position = new(4f, 1f, 5f);

        EnemyRenderState before = EnemyRenderState.CreateWolf(position, Vector3.UnitX, EnemyBehaviorState.Chasing);
        EnemyRenderState after = EnemyRenderState.CreateWolf(
            position, Vector3.UnitX, EnemyBehaviorState.Chasing, WolfAssetDefinition.VisualScale * visual.ScaleMultiplier);

        Assert.Equal(before, after);
        Assert.Equal(Vector3.Zero, visual.TintOffset);
    }

    [Fact]
    public void DireWolfVisual_IsLargerThanTheWolf()
    {
        EnemyVisual dire = EnemyVisualCatalog.Resolve("enemy.dire-wolf");
        EnemyRenderState wolfState = EnemyRenderState.CreateWolf(Vector3.Zero, -Vector3.UnitZ, EnemyBehaviorState.Idle);
        EnemyRenderState direState = EnemyRenderState.CreateWolf(
            Vector3.Zero, -Vector3.UnitZ, EnemyBehaviorState.Idle, WolfAssetDefinition.VisualScale * dire.ScaleMultiplier);

        Assert.Equal(wolfState.ModelPath, direState.ModelPath);
        Assert.Equal(wolfState.ModelMatrix.M11 * 1.35f, direState.ModelMatrix.M11, 3);
    }

    // The opened indirection: the model, the clip set and the scale all come from the visual, so
    // the hound draws its own model and its own clips without a branch in the renderer.
    [Fact]
    public void TheHoundVisual_DrawsItsOwnModel_AndItsOwnClips()
    {
        EnemyVisual hound = EnemyVisualCatalog.Resolve(EnemyVisualCatalog.HoundKey);

        EnemyRenderState chasing = EnemyRenderState.Create(
            in hound, new Vector3(2f, 0f, 3f), Vector3.UnitX, EnemyBehaviorState.Chasing);
        EnemyRenderState dying = EnemyRenderState.Create(
            in hound, Vector3.Zero, Vector3.UnitX, EnemyBehaviorState.Dead);

        Assert.Equal(BeastAssetDefinition.HoundModelPath, chasing.ModelPath);
        Assert.Equal(BeastAssetDefinition.MoveClip, chasing.ClipName);
        Assert.Equal(BeastAssetDefinition.DieClip, dying.ClipName);
        Assert.Equal("enemy", chasing.ShaderName);
        Assert.Equal(2f, chasing.ModelMatrix.M41, 3);
        Assert.Equal(hound.ResolveScale(), new Vector3(chasing.ModelMatrix.M11, chasing.ModelMatrix.M12, chasing.ModelMatrix.M13).Length, 3);
    }

    [Fact]
    public void Create_WithTheDefaultVisual_IsCreateWolf()
    {
        EnemyVisual wolf = EnemyVisualCatalog.Resolve(null);
        Vector3 position = new(1f, 2f, 3f);

        Assert.Equal(
            EnemyRenderState.CreateWolf(position, Vector3.UnitX, EnemyBehaviorState.Attacking),
            EnemyRenderState.Create(in wolf, position, Vector3.UnitX, EnemyBehaviorState.Attacking));
    }

    [Fact]
    public void DireWolfVisual_ScalesFromItsOwnVisual_WithoutTheRendererDoingTheMultiply()
    {
        EnemyVisual dire = EnemyVisualCatalog.Resolve("enemy.dire-wolf");

        EnemyRenderState direState = EnemyRenderState.Create(
            in dire, Vector3.Zero, -Vector3.UnitZ, EnemyBehaviorState.Idle);

        Assert.Equal(WolfAssetDefinition.VisualScale * 1.35f, dire.ResolveScale(), 4);
        Assert.Equal(
            dire.ResolveScale(),
            new Vector3(direState.ModelMatrix.M11, direState.ModelMatrix.M12, direState.ModelMatrix.M13).Length,
            4);
    }

    // Contract C-F: the speed is the LAST parameter and optional, so every call site above still means
    // "no measured speed" - a walk at the authored rate.
    [Fact]
    public void WithNoSpeed_AChasingEnemyWalksAtTheAuthoredRate()
    {
        EnemyVisual wolf = EnemyVisualCatalog.Resolve(null);

        EnemyRenderState state = EnemyRenderState.Create(in wolf, Vector3.Zero, Vector3.UnitX, EnemyBehaviorState.Chasing);

        Assert.Equal(WolfAssetDefinition.WalkClip, state.ClipName);
        Assert.Equal(1f, state.PlaybackSpeed, 4);
    }

    [Fact]
    public void AMeasuredSpeed_PicksTheGait_AndTheRateItPlaysAt()
    {
        EnemyVisual wolf = EnemyVisualCatalog.Resolve(null);

        EnemyRenderState patrolling = EnemyRenderState.Create(
            in wolf, Vector3.Zero, Vector3.UnitX, EnemyBehaviorState.Chasing, scale: null, speed: 1.4f);
        EnemyRenderState chasing = EnemyRenderState.Create(
            in wolf, Vector3.Zero, Vector3.UnitX, EnemyBehaviorState.Chasing, scale: null, speed: 5.0f);

        Assert.Equal(WolfAssetDefinition.WalkClip, patrolling.ClipName);
        Assert.Equal(1f, patrolling.PlaybackSpeed, 4);
        Assert.Equal(WolfAssetDefinition.GallopClip, chasing.ClipName);
        Assert.Equal(1f, chasing.PlaybackSpeed, 4);
        // Faster than the gallop's reference speed: the same clip, played faster.
        EnemyRenderState hurried = EnemyRenderState.Create(
            in wolf, Vector3.Zero, Vector3.UnitX, EnemyBehaviorState.Chasing, scale: null, speed: 6.0f);
        Assert.Equal(WolfAssetDefinition.GallopClip, hurried.ClipName);
        Assert.Equal(1.2f, hurried.PlaybackSpeed, 4);
    }

    [Fact]
    public void TheWolfsDeath_IsItsOwnClip_NotItsIdle()
    {
        EnemyVisual wolf = EnemyVisualCatalog.Resolve(null);

        EnemyRenderState dying = EnemyRenderState.Create(in wolf, Vector3.Zero, Vector3.UnitX, EnemyBehaviorState.Dead);

        Assert.Equal(WolfAssetDefinition.DieClip, dying.ClipName);
        Assert.NotEqual(WolfAssetDefinition.IdleClip, dying.ClipName);
        Assert.Equal(1f, dying.PlaybackSpeed, 4);
    }

    [Theory]
    [InlineData(0f, 0f, -1f)]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 0f, 1f)]
    public void CreateWolf_Rotates_Authored_Forward_Toward_Movement(float x, float y, float z)
    {
        Vector3 desiredForward = new(x, y, z);
        EnemyRenderState state = EnemyRenderState.CreateWolf(
            Vector3.Zero,
            desiredForward,
            EnemyBehaviorState.Chasing,
            scale: 1f);

        Vector3 actualForward = Vector4.TransformRow(
            new Vector4(WolfAssetDefinition.AuthoredForwardAxis, 0f),
            state.ModelMatrix).Xyz.Normalized();

        Assert.Equal(desiredForward.X, actualForward.X, 3);
        Assert.Equal(desiredForward.Y, actualForward.Y, 3);
        Assert.Equal(desiredForward.Z, actualForward.Z, 3);
    }
}
