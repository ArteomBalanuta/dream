using System.IO;
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
    public void IsCaptureRun_MenuFlowZeroIsNotACaptureRun()
    {
        Assert.False(VSyncSettings.IsCaptureRun(name => name == "DREAM_MENU_FLOW" ? "0" : null));
    }

    [Theory]
    [InlineData(VSyncMode.Off, true, 0)]
    [InlineData(VSyncMode.Off, false, 0)]
    [InlineData(VSyncMode.On, true, 1)]
    [InlineData(VSyncMode.On, false, 1)]
    [InlineData(VSyncMode.Adaptive, true, -1)]
    [InlineData(VSyncMode.Adaptive, false, 1)]
    public void SwapInterval_AdaptiveNeedsTheTearExtension(VSyncMode mode, bool tear, int expected)
    {
        Assert.Equal(expected, VSyncSettings.SwapInterval(mode, tear));
    }

    [Fact]
    public void SwapInterval_ACaptureRunNeverSyncs_WhateverTheEnvironmentSays()
    {
        VSyncMode resolved = VSyncSettings.Resolve(VSyncMode.Adaptive, "adaptive", captureRun: true);
        Assert.Equal(0, VSyncSettings.SwapInterval(resolved, tearSupported: true));
    }

    [Fact]
    public void Game_NeverLetsOpenTkTogglesTheInterval()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "dream", "dream.csproj")))
        {
            dir = Path.GetDirectoryName(dir)!;
        }

        string source = File.ReadAllText(Path.Combine(dir, "dream", "Application", "Game.cs"));
        Assert.Null(PacingViolation(source));

        // Synthetic negatives: each pattern must be able to fail on a mutated copy.
        Assert.NotNull(PacingViolation(source.Replace("VSync = VSyncMode.Off;", "VSync = VSyncMode.Adaptive;")));
        Assert.NotNull(PacingViolation(source.Replace("VSync = VSyncMode.Off;", "VSync = mode;")));
        Assert.NotNull(PacingViolation(source.Replace("VSync = VSyncMode.Off;", "// removed")));
        const string swapCall = "GLFW.SwapInterval(VSyncSettings.SwapInterval(mode, AdaptiveSyncSupported()));";
        Assert.Contains(swapCall, source);
        string swapFirst = source.Replace("VSync = VSyncMode.Off;", "// moved").Replace(swapCall, swapCall + " VSync = VSyncMode.Off;");
        Assert.NotNull(PacingViolation(swapFirst));

        // Negatives that keep the Off literal in place, so the two later checks are shown to be able to fail on their own.
        Assert.Contains("assigned something other", PacingViolation(source.Replace(swapCall, swapCall + " VSync = VSyncMode.On;")));
        Assert.Contains("must come from VSyncSettings", PacingViolation(source.Replace(swapCall, "GLFW.SwapInterval(1);")));
    }

    // Null when ApplyFramePacing sets OpenTK's VSync to the Off literal before the direct swap interval,
    // and nothing else assigns VSync anywhere in the file.
    private static string? PacingViolation(string source)
    {
        int start = source.IndexOf("void ApplyFramePacing(", StringComparison.Ordinal);
        if (start < 0)
        {
            return "ApplyFramePacing not found";
        }

        int end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        string body = source.Substring(start, end < 0 ? source.Length - start : end - start);
        int off = body.IndexOf("VSync = VSyncMode.Off;", StringComparison.Ordinal);
        int swap = body.IndexOf("GLFW.SwapInterval(", StringComparison.Ordinal);
        if (off < 0 || swap < 0 || off >= swap)
        {
            return "VSync = Off must come before GLFW.SwapInterval inside ApplyFramePacing";
        }

        if (!body.Contains("GLFW.SwapInterval(VSyncSettings.SwapInterval(", StringComparison.Ordinal))
        {
            return "the swap interval must come from VSyncSettings.SwapInterval";
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(source, @"\bVSync\s*=(?!\s*VSyncMode\.Off;)"))
        {
            return "VSync is assigned something other than the Off literal";
        }

        return null;
    }

    [Fact]
    public void Describe_NamesEveryMode()
    {
        Assert.Equal("off", VSyncSettings.Describe(VSyncMode.Off));
        Assert.Equal("on", VSyncSettings.Describe(VSyncMode.On));
        Assert.Equal("adaptive", VSyncSettings.Describe(VSyncMode.Adaptive));
    }
}
