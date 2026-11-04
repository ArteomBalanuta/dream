using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Actors;

public sealed class EnemyVisualCatalogTests
{
    [Fact]
    public void TheWolf_IsUnscaled_AndUntinted()
    {
        EnemyVisual wolf = EnemyVisualCatalog.Resolve("enemy.wolf");

        Assert.Equal(WolfAssetDefinition.ModelPath, wolf.ModelPath);
        Assert.Equal(1.0f, wolf.ScaleMultiplier);
        Assert.Equal(Vector3.Zero, wolf.TintOffset);
    }

    [Fact]
    public void TheDireWolf_IsLarger_AndDarker()
    {
        EnemyVisual dire = EnemyVisualCatalog.Resolve("enemy.dire-wolf");

        Assert.Equal(WolfAssetDefinition.ModelPath, dire.ModelPath);
        Assert.Equal(1.35f, dire.ScaleMultiplier);
        Assert.Equal(new Vector3(-0.45f, -0.45f, -0.35f), dire.TintOffset);
    }

    [Fact]
    public void TheHound_IsItsOwnModel_WithItsOwnClips()
    {
        EnemyVisual hound = EnemyVisualCatalog.Resolve(EnemyVisualCatalog.HoundKey);

        Assert.Equal(BeastAssetDefinition.HoundModelPath, hound.ModelPath);
        Assert.NotEqual(WolfAssetDefinition.ModelPath, hound.ModelPath);
        Assert.Equal(BeastAssetDefinition.IdleClip, hound.Clips.Idle);
        Assert.Equal(BeastAssetDefinition.MoveClip, hound.Clips.Move);
        Assert.Equal(BeastAssetDefinition.AttackClip, hound.Clips.Attack);
        // The wolf pack has no death clip; the forged hound does.
        Assert.Equal(BeastAssetDefinition.DieClip, hound.Clips.Die);
        Assert.NotEqual(hound.Clips.Idle, hound.Clips.Die);
    }

    // The hound took GraveHowler's key off enemy.dire-wolf, so it must keep the dire wolf's size and
    // tint: the model changed, the enemy did not. Both models normalise to the same world height, so
    // a 1.0 multiplier here would quietly draw the level-8 top-tier enemy at the plain wolf's size.
    [Fact]
    public void TheHound_KeepsTheDireWolfsSizeAndTint_BecauseOnlyTheModelChanged()
    {
        EnemyVisual hound = EnemyVisualCatalog.Resolve(EnemyVisualCatalog.HoundKey);
        EnemyVisual dire = EnemyVisualCatalog.Resolve(EnemyVisualCatalog.DireWolfKey);

        Assert.Equal(dire.ScaleMultiplier, hound.ScaleMultiplier);
        Assert.Equal(dire.TintOffset, hound.TintOffset);
        Assert.Equal(1.35f, hound.ScaleMultiplier);
        Assert.Equal(new Vector3(-0.45f, -0.45f, -0.35f), hound.TintOffset);
        // And it is not the plain wolf's look, which is what the regression looked like.
        Assert.NotEqual(EnemyVisualCatalog.Default.ScaleMultiplier, hound.ScaleMultiplier);
        Assert.NotEqual(EnemyVisualCatalog.Default.TintOffset, hound.TintOffset);
    }

    [Fact]
    public void TheWolfsClips_KeepTheirPackPrefix_AndItsDeathHoldsTheIdle()
    {
        EnemyVisual wolf = EnemyVisualCatalog.Resolve(null);

        Assert.Equal("AnimalArmature|Idle", wolf.Clips.Idle);
        Assert.Equal("AnimalArmature|Walk", wolf.Clips.Move);
        Assert.Equal(wolf.Clips.Idle, wolf.Clips.Die);
        Assert.Equal(wolf.Clips.Move, wolf.Clips.Resolve(Dream.Simulation.Enemies.EnemyBehaviorState.Chasing));
        Assert.Equal(wolf.Clips.Attack, wolf.Clips.Resolve(Dream.Simulation.Enemies.EnemyBehaviorState.Attacking));
        Assert.Equal(wolf.Clips.Die, wolf.Clips.Resolve(Dream.Simulation.Enemies.EnemyBehaviorState.Dead));
        Assert.Equal(wolf.Clips.Idle, wolf.Clips.Resolve(Dream.Simulation.Enemies.EnemyBehaviorState.Idle));
    }

    // Only GraveHowler moves to the hound this wave; the other three templates keep the wolf.
    [Fact]
    public void OnlyTheGraveHowler_PointsAtTheHound()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "dream", "config", "content", "enemies.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        string json = File.ReadAllText(Path.Combine(root!, "dream", "config", "content", "enemies.json"));

        Assert.Equal(1, json.Split("enemy.hound", StringSplitOptions.None).Length - 1);
        int howler = json.IndexOf("GraveHowler", StringComparison.Ordinal);
        int hound = json.IndexOf("enemy.hound", StringComparison.Ordinal);
        Assert.True(howler > 0 && hound > howler);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("enemy.unknown")]
    [InlineData("ENEMY.DIRE-WOLF")]
    [InlineData("enemy.dire-wolf ")]
    public void UnknownOrNullKeys_DrawTheDefaultWolf(string? key)
    {
        Assert.Equal(EnemyVisualCatalog.Resolve("enemy.wolf"), EnemyVisualCatalog.Resolve(key));
    }

    [Fact]
    public void AnOversizedKey_DrawsTheDefaultWolf_AndDoesNotThrow()
    {
        string huge = new('d', 10_000);

        EnemyVisual visual = default;
        Exception? thrown = Record.Exception(() => visual = EnemyVisualCatalog.Resolve(huge));

        Assert.Null(thrown);
        Assert.Equal(EnemyVisualCatalog.Resolve(null), visual);
    }

    [Fact]
    public void Resolving_AllocatesNothing()
    {
        Dream.Tests.Utilities.AllocationAssert.AtMost(0, () =>
        {
            _ = EnemyVisualCatalog.Resolve("enemy.dire-wolf");
            _ = EnemyVisualCatalog.Resolve(null);
            _ = EnemyVisualCatalog.Resolve("nope");
        });
    }

    [Fact]
    public void TheShader_AddsAnOffsetThatIsHarmlessWhenUnset_InBothVariants()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "dream", "shaders", "enemyFragment.glsl")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        string source = File.ReadAllText(Path.Combine(root!, "dream", "shaders", "enemyFragment.glsl"));

        Assert.Contains("uniform vec3 uActorTintOffset;", source, StringComparison.Ordinal);
        // base * (1 + offset): an unset uniform is zero, so the multiplier is exactly one.
        Assert.Contains("vec3(1.0) + uActorTintOffset", source, StringComparison.Ordinal);
        // The tint sits before the MASK discard path, so the one code path serves both variants.
        Assert.True(
            source.IndexOf("vec3(1.0) + uActorTintOffset", StringComparison.Ordinal) <
            source.IndexOf("vec3 n = normalize", StringComparison.Ordinal));
    }

    // GL cannot open in a test: the reset itself is checked by a person (players must not darken
    // after a Dire Wolf is drawn). This pins that the call is still in the renderer.
    [Fact]
    public void TheEnemyRenderer_ResetsTheTintAfterTheEnemies()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "dream", "Rendering", "Actors", "EnemyRenderer.cs")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        string source = File.ReadAllText(Path.Combine(root!, "dream", "Rendering", "Actors", "EnemyRenderer.cs"));

        int reset = source.IndexOf("ResetTint();", StringComparison.Ordinal);
        Assert.True(reset > 0);
        Assert.True(reset < source.IndexOf("_visualStates.PruneExcept", StringComparison.Ordinal));
        // The reset covers the base program and its MASK variant.
        Assert.Contains("maskProgram.SetVector3(TintUniformName, Vector3.Zero)", source, StringComparison.Ordinal);
    }
}
