using Dream.Configuration;
using OpenTK.Windowing.Common;
using Xunit;

namespace Dream.Tests.Configuration;

public class VSyncSettingsTests
{
    [Fact]
    public void Default_IsAdaptive_AndSoIsANewConfig()
    {
        Assert.Equal(VSyncMode.Adaptive, VSyncSettings.Default);
        Assert.Equal(VSyncSettings.Default, new DesktopClientConfig().VSync);
        Assert.Equal("DREAM_VSYNC", VSyncSettings.EnvironmentVariable);
    }

    [Theory]
    [InlineData("off", VSyncMode.Off)]
    [InlineData("OFF", VSyncMode.Off)]
    [InlineData("0", VSyncMode.Off)]
    [InlineData(" on ", VSyncMode.On)]
    [InlineData("On", VSyncMode.On)]
    [InlineData("1", VSyncMode.On)]
    [InlineData("adaptive", VSyncMode.Adaptive)]
    [InlineData("  Adaptive\t", VSyncMode.Adaptive)]
    public void Parse_AcceptsTheNamesAndDigits(string text, VSyncMode expected)
    {
        Assert.Equal(expected, VSyncSettings.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("maybe")]
    public void Parse_RejectsEverythingElse(string? text)
    {
        Assert.Null(VSyncSettings.Parse(text));
    }

    [Fact]
    public void Resolve_EnvironmentOverridesConfig_AndInvalidEnvironmentFallsBackToConfig()
    {
        Assert.Equal(VSyncMode.Off, VSyncSettings.Resolve(VSyncMode.On, "off", captureRun: false));
        Assert.Equal(VSyncMode.On, VSyncSettings.Resolve(VSyncMode.Off, "1", captureRun: false));
        Assert.Equal(VSyncMode.On, VSyncSettings.Resolve(VSyncMode.On, "bogus", captureRun: false));
        Assert.Equal(VSyncMode.Off, VSyncSettings.Resolve(VSyncMode.Off, null, captureRun: false));
    }

    [Fact]
    public void Resolve_UndefinedConfigValueBecomesTheDefault()
    {
        Assert.Equal(VSyncSettings.Default, VSyncSettings.Resolve((VSyncMode)99, null, captureRun: false));
    }

    [Theory]
    [InlineData(VSyncMode.On, null)]
    [InlineData(VSyncMode.Adaptive, "on")]
    [InlineData(VSyncMode.Adaptive, "adaptive")]
    [InlineData(VSyncMode.Off, "1")]
    public void Resolve_ACaptureRunIsAlwaysOff(VSyncMode config, string? environment)
    {
        Assert.Equal(VSyncMode.Off, VSyncSettings.Resolve(config, environment, captureRun: true));
    }

    [Theory]
    [InlineData("DREAM_HUD_CAPTURE")]
    [InlineData("DREAM_MENU_CAPTURE")]
    [InlineData("DREAM_MENU_FLOW")]
    public void IsCaptureRun_IsTrueForEachCaptureVariable_AndFalseWhenBlank(string variable)
    {
        Assert.True(VSyncSettings.IsCaptureRun(name => name == variable ? "x" : null));
        Assert.False(VSyncSettings.IsCaptureRun(name => name == variable ? "  " : null));
        Assert.False(VSyncSettings.IsCaptureRun(_ => null));
    }

    [Fact]
    public void Describe_NamesEveryMode()
    {
        Assert.Equal("off", VSyncSettings.Describe(VSyncMode.Off));
        Assert.Equal("on", VSyncSettings.Describe(VSyncMode.On));
        Assert.Equal("adaptive", VSyncSettings.Describe(VSyncMode.Adaptive));
    }
}
