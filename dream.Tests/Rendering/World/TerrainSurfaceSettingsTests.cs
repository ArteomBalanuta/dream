using Dream.Rendering.World;
using Xunit;

namespace Dream.Tests.Rendering.World;

public class TerrainSurfaceSettingsTests
{
    [Fact]
    public void One_Enables_The_Variation()
    {
        TerrainSurfaceSettings settings = TerrainSurfaceSettings.Parse("1");

        Assert.True(settings.Enabled);
        Assert.Equal(1f, settings.Variation);
    }

    [Fact]
    public void Zero_Disables_The_Variation()
    {
        TerrainSurfaceSettings settings = TerrainSurfaceSettings.Parse(" 0 ");

        Assert.False(settings.Enabled);
        Assert.Equal(0f, settings.Variation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_Keeps_The_Default_Without_A_Warning(string? value)
    {
        List<string> warnings = [];

        TerrainSurfaceSettings settings = TerrainSurfaceSettings.Parse(value, warnings.Add);

        Assert.Equal(TerrainSurfaceSettings.Default, settings);
        Assert.True(settings.Enabled);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("on")]
    [InlineData("-1")]
    public void Invalid_Keeps_The_Default_And_Warns_Once(string value)
    {
        List<string> warnings = [];

        TerrainSurfaceSettings settings = TerrainSurfaceSettings.Parse(value, warnings.Add);

        Assert.Equal(TerrainSurfaceSettings.Default, settings);
        string warning = Assert.Single(warnings);
        Assert.Contains(TerrainSurfaceSettings.EnvironmentVariable, warning);
        Assert.Contains(value, warning);
    }

    [Fact]
    public void Invalid_Without_A_Warn_Callback_Does_Not_Throw()
    {
        Assert.Equal(TerrainSurfaceSettings.Default, TerrainSurfaceSettings.Parse("nope"));
    }

    [Theory]
    [InlineData("01")]
    [InlineData("1.0")]
    [InlineData("true")]
    public void Near_Misses_Are_Invalid_Not_Parsed_Leniently(string value)
    {
        List<string> warnings = [];

        Assert.Equal(TerrainSurfaceSettings.Default, TerrainSurfaceSettings.Parse(value, warnings.Add));
        Assert.Single(warnings);
    }

    [Fact]
    public void Default_Is_Enabled_And_Variation_Matches_Enabled()
    {
        Assert.True(TerrainSurfaceSettings.Default.Enabled);
        Assert.Equal(1f, TerrainSurfaceSettings.Default.Variation);
        Assert.Equal(0f, new TerrainSurfaceSettings(false).Variation);
        Assert.True(TerrainSurfaceSettings.Default.Blend);
        Assert.True(TerrainSurfaceSettings.Default.Relief);
    }

    [Theory]
    [InlineData(null, null, null, true, true, true)]
    [InlineData("1", "1", "1", true, true, true)]
    [InlineData("1", "0", "1", true, false, true)]
    [InlineData("1", "1", " 0 ", true, true, false)]
    [InlineData("1", "0", "0", true, false, false)]
    [InlineData("0", null, null, false, false, false)]
    [InlineData("0", "1", "1", false, false, false)]
    public void Blend_And_Relief_Parse_And_Are_Forced_Off_With_The_Variation(
        string? variation, string? blend, string? relief, bool enabled, bool expectedBlend, bool expectedRelief)
    {
        List<string> warnings = [];

        TerrainSurfaceSettings settings = TerrainSurfaceSettings.Parse(variation, blend, relief, warnings.Add);

        Assert.Equal(enabled, settings.Enabled);
        Assert.Equal(expectedBlend, settings.Blend);
        Assert.Equal(expectedRelief, settings.Relief);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("yes", "DREAM_TERRAIN_BLEND")]
    [InlineData("2", "DREAM_TERRAIN_BLEND")]
    [InlineData("on", "DREAM_TERRAIN_RELIEF")]
    public void Invalid_Blend_Or_Relief_Warns_Once_And_Keeps_The_Default(string value, string name)
    {
        List<string> warnings = [];
        bool blend = name == TerrainSurfaceSettings.BlendEnvironmentVariable;

        TerrainSurfaceSettings settings = TerrainSurfaceSettings.Parse(
            null,
            blend ? value : null,
            blend ? null : value,
            warnings.Add);

        Assert.Equal(TerrainSurfaceSettings.Default, settings);
        string warning = Assert.Single(warnings);
        Assert.Contains(name, warning);
        Assert.Contains(value, warning);
    }
}
