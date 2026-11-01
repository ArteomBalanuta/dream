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
