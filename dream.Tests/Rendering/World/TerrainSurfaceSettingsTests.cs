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
    }
}
