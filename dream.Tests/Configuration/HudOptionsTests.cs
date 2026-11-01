using System.IO;
using OpenTK.Mathematics;
using Xunit;
using Dream.Application;
using Dream.Configuration;

namespace Dream.Tests.Configuration;

public class HudOptionsTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(" ON ", true)]
    [InlineData("0", false)]
    [InlineData("False", false)]
    [InlineData("no", false)]
    public void ParseFlag_ReadsOnAndOff(string value, bool expected)
    {
        Assert.Equal(expected, HudOptions.ParseFlag(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("maybe")]
    [InlineData("2")]
    public void ParseFlag_IgnoresAnythingElse(string? value)
    {
        Assert.Null(HudOptions.ParseFlag(value));
    }

    [Fact]
    public void Resolve_ConfigAloneDecidesWithoutAnOverride()
    {
        Assert.False(HudOptions.Resolve(false, 0f, null, null).AdminHudAvailable);
        Assert.True(HudOptions.Resolve(true, 0f, null, null).AdminHudAvailable);
    }

    [Fact]
    public void Resolve_EnvOverridesTheConfigBothWays_AndGarbageIsIgnored()
    {
        Assert.True(HudOptions.Resolve(false, 0f, "1", null).AdminHudAvailable);
        Assert.False(HudOptions.Resolve(true, 0f, "0", null).AdminHudAvailable);
        Assert.True(HudOptions.Resolve(true, 0f, "nonsense", null).AdminHudAvailable);
    }

    [Theory]
    [InlineData("imgui", true)]
    [InlineData(" ImGui ", true)]
    [InlineData("classic", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Resolve_DreamHudImguiForcesTheImGuiSurface(string? value, bool expected)
    {
        Assert.Equal(expected, HudOptions.Resolve(false, 0f, null, value).ForceImGuiSurface);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(-2f, 0f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(0.01f, 1f)]
    [InlineData(1.9f, 2f)]
    [InlineData(2.3f, 2.25f)]
    [InlineData(9f, 4f)]
    public void SanitizeUiScale_IsAutoOrAQuarterStepInRange(float value, float expected)
    {
        Assert.Equal(expected, HudOptions.SanitizeUiScale(value));
    }

    [Fact]
    public void ConfigFile_DefaultsWriteAdminHudOffAndAutoScale()
    {
        string text = DesktopClientTextConfig.Serialize(new DesktopClientConfig());

        Assert.Contains("admin_hud=0", text);
        Assert.Contains("ui_scale=0", text);
    }

    [Fact]
    public void ConfigFile_RoundTripsAdminHudAndUiScale()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(dir, "desktop-client.txt");
        DesktopClientTextConfig.Save(path, new DesktopClientConfig { AdminHud = true, UiScale = 2.25f });

        DesktopClientConfig loaded = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.True(loaded.AdminHud);
        Assert.Equal(2.25f, loaded.UiScale);
    }

    [Fact]
    public void ConfigFile_OldFilesWithoutTheKeysLoadWithDefaults()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(dir, "desktop-client.txt");
        File.WriteAllText(path, "host=127.0.0.1\nmax_fps=144\n");

        DesktopClientConfig loaded = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.False(loaded.AdminHud);
        Assert.Equal(0f, loaded.UiScale);
    }

    [Fact]
    public void ConfigFile_BadValuesFallBackToTheDefaults()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(dir, "desktop-client.txt");
        File.WriteAllText(path, "admin_hud=banana\nui_scale=-3\n");

        DesktopClientConfig loaded = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.False(loaded.AdminHud);
        Assert.Equal(0f, loaded.UiScale);
    }

    [Fact]
    public void EnvOverride_IsNeverWrittenBackToTheConfig()
    {
        DesktopClientConfig config = new() { AdminHud = false };
        HudOptions forThisRun = HudOptions.Resolve(config.AdminHud, config.UiScale, "1", "imgui");
        GameSettings settings = new(new Vector2i(1440, 1024), clientConfig: config, hudOptions: forThisRun);

        config.CaptureRuntime(settings);
        string text = DesktopClientTextConfig.Serialize(config);

        Assert.True(settings.AdminHudAvailable);
        Assert.True(settings.ForceImGuiHud);
        Assert.False(config.AdminHud);
        Assert.Contains("admin_hud=0", text);
    }

    [Fact]
    public void UiScalePick_IsSavedWithTheRuntimeSettings()
    {
        DesktopClientConfig config = new();
        GameSettings settings = new(new Vector2i(1440, 1024), clientConfig: config, hudOptions: HudOptions.Resolve(false, 0f, null, null));

        settings.SetUiScale(2.3f);
        config.CaptureRuntime(settings);

        Assert.Equal(2.25f, settings.UiScaleSetting);
        Assert.Equal(2.25f, config.UiScale);
        // 2.25 is saved as picked, but 1440x1024 only fits 2.0, so that is the scale in use.
        Assert.Equal(2.0f, settings.ResolvedUiScale);
    }

    [Fact]
    public void GameSettings_AutoScaleFollowsTheWindow()
    {
        GameSettings settings = new(new Vector2i(1920, 1080), hudOptions: HudOptions.Resolve(false, 0f, null, null));

        Assert.Equal(2.25f, settings.ResolvedUiScale);
        settings.ApplyResolution(new Vector2i(1280, 720));
        Assert.Equal(1.5f, settings.ResolvedUiScale);
    }

    [Fact]
    public void GameSettings_AdminIsOffByDefault()
    {
        GameSettings settings = new(new Vector2i(1440, 1024), hudOptions: HudOptions.Resolve(false, 0f, null, null));

        Assert.False(settings.AdminHudAvailable);
        Assert.False(settings.ForceImGuiHud);
    }
}
