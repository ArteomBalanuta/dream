using System.IO;
using Dream.Application;
using Dream.Client.CameraControl;
using Dream.Configuration;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Configuration;

public class TacticalCameraConfigTests
{
    private static DesktopClientConfig Load(params string[] lines)
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "desktop-client.txt");
        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        return DesktopClientTextConfig.LoadOrCreate(path);
    }

    [Fact]
    public void Defaults_AreTheContractDefaults()
    {
        Assert.Equal(TacticalCameraSettings.Default.Sanitized(), new DesktopClientConfig().TacticalCamera);
        Assert.Equal(TacticalCameraSettings.Default.Sanitized(), new GameSettings(new Vector2i(1280, 720)).TacticalCamera);
    }

    [Fact]
    public void EveryKey_RoundTripsThroughSerializeAndLoad()
    {
        TacticalCameraSettings custom = TacticalCameraSettings.Default with
        {
            EdgePanEnabled = false,
            EdgeMarginPixels = 24f,
            PanSpeed = 22f,
            ConfineMode = CameraConfineMode.Always,
            AnchorMode = CameraAnchorMode.Free,
            ReturnToPlayerSeconds = 3.5f,
            RotateDegreesPerPixel = 0.8f,
            RotateSnapStepDegrees = 30f,
            SnapOnDragRelease = true,
            ZoomStepRatio = 1.3f,
            InvertZoom = true
        };
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "desktop-client.txt");
        DesktopClientTextConfig.Save(path, new DesktopClientConfig { TacticalCamera = custom });

        string text = File.ReadAllText(path);
        foreach (string key in new[]
        {
            "camera_edge_pan=0", "camera_edge_margin=24", "camera_pan_speed=22", "camera_confine=always",
            "camera_anchor=free", "camera_return_seconds=3.5", "camera_rotate_sensitivity=0.8", "camera_snap_step=30",
            "camera_snap_on_release=1", "camera_zoom_step=1.3", "camera_invert_zoom=1"
        })
        {
            Assert.Contains(key, text);
        }

        TacticalCameraSettings loaded = DesktopClientTextConfig.LoadOrCreate(path).TacticalCamera;
        Assert.Equal(custom.Sanitized(), loaded);
    }

    [Theory]
    [InlineData("off", CameraConfineMode.Off)]
    [InlineData("fullscreen", CameraConfineMode.FullscreenOnly)]
    [InlineData("always", CameraConfineMode.Always)]
    [InlineData("banana", CameraConfineMode.FullscreenOnly)]
    public void Confine_ParsesNames_AndUnknownFallsBack(string value, CameraConfineMode expected)
    {
        Assert.Equal(expected, Load("camera_confine=" + value).TacticalCamera.ConfineMode);
    }

    [Theory]
    [InlineData("free", CameraAnchorMode.Free)]
    [InlineData("follow", CameraAnchorMode.Follow)]
    [InlineData("sideways", CameraAnchorMode.Follow)]
    public void Anchor_ParsesNames_AndUnknownFallsBack(string value, CameraAnchorMode expected)
    {
        Assert.Equal(expected, Load("camera_anchor=" + value).TacticalCamera.AnchorMode);
    }

    [Fact]
    public void OutOfRangeAndNonFiniteValues_AreSanitisedNeverThrow()
    {
        TacticalCameraSettings camera = Load(
            "camera_edge_margin=9999",
            "camera_pan_speed=-5",
            "camera_return_seconds=NaN",
            "camera_rotate_sensitivity=0",
            "camera_snap_step=50",
            "camera_zoom_step=99").TacticalCamera;

        Assert.Equal(48f, camera.EdgeMarginPixels);
        Assert.Equal(4f, camera.PanSpeed);
        Assert.Equal(0f, camera.ReturnToPlayerSeconds);
        Assert.Equal(0.05f, camera.RotateDegreesPerPixel);
        Assert.Equal(45f, camera.RotateSnapStepDegrees);
        Assert.Equal(1.5f, camera.ZoomStepRatio);
    }

    [Fact]
    public void GarbageValues_KeepTheDefaults()
    {
        TacticalCameraSettings camera = Load(
            "camera_edge_pan=maybe",
            "camera_edge_margin=wide",
            "camera_pan_speed=",
            "camera_snap_on_release=perhaps",
            "camera_invert_zoom=2x").TacticalCamera;

        TacticalCameraSettings expected = TacticalCameraSettings.Default.Sanitized();
        Assert.Equal(expected.EdgePanEnabled, camera.EdgePanEnabled);
        Assert.Equal(expected.EdgeMarginPixels, camera.EdgeMarginPixels);
        Assert.Equal(expected.PanSpeed, camera.PanSpeed);
        Assert.Equal(expected.SnapOnDragRelease, camera.SnapOnDragRelease);
        Assert.Equal(expected.InvertZoom, camera.InvertZoom);
    }

    [Fact]
    public void UndefinedEnumsAndNullCamera_AreSanitised()
    {
        DesktopClientConfig config = new()
        {
            TacticalCamera = TacticalCameraSettings.Default with
            {
                ConfineMode = (CameraConfineMode)77,
                AnchorMode = (CameraAnchorMode)9,
                PanSpeed = float.PositiveInfinity
            }
        };
        config.Sanitize();
        Assert.Equal(CameraConfineMode.FullscreenOnly, config.TacticalCamera.ConfineMode);
        Assert.Equal(CameraAnchorMode.Follow, config.TacticalCamera.AnchorMode);
        Assert.Equal(14f, config.TacticalCamera.PanSpeed);

        config.TacticalCamera = null!;
        config.Sanitize();
        Assert.NotNull(config.TacticalCamera);
    }

    [Fact]
    public void GameSettings_SetterSanitises_AndTheConfigSeedsIt_AndCaptureRuntimeSavesIt()
    {
        GameSettings settings = new(new Vector2i(1280, 720));
        settings.SetTacticalCamera(TacticalCameraSettings.Default with { PanSpeed = 500f, RotateSnapStepDegrees = 44f });
        Assert.Equal(40f, settings.TacticalCamera.PanSpeed);
        Assert.Equal(45f, settings.TacticalCamera.RotateSnapStepDegrees);

        DesktopClientConfig config = new();
        config.CaptureRuntime(settings);
        Assert.Equal(settings.TacticalCamera, config.TacticalCamera);

        GameSettings seeded = new(new Vector2i(1280, 720), clientConfig: config);
        Assert.Equal(settings.TacticalCamera, seeded.TacticalCamera);
    }
}
