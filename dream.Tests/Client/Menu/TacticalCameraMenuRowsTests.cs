using Dream.Application;
using Dream.Client.CameraControl;
using Dream.Client.Menu;
using Dream.Configuration;
using Dream.Rendering.Ui.Menu;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Menu;

public class TacticalCameraMenuRowsTests
{
    private static readonly string[] Labels =
    [
        "Edge Pan", "Edge Margin", "Pan Speed", "Cursor Confine", "Free Camera (Y)", "Return To Player",
        "Rotate Sensitivity", "Rotate Snap Step", "Snap On Release", "Zoom Speed", "Invert Zoom"
    ];

    private static (List<MenuOptionRow> Rows, GameSettings Settings, DesktopClientConfig Config, int[] Persisted) Create()
    {
        GameSettings settings = new(new Vector2i(1440, 1024));
        DesktopClientConfig config = new();
        int[] persisted = new int[1];
        List<MenuOptionRow> rows = MenuOptionRows.CreateCameraRows(settings, config, () => persisted[0]++);
        return (rows, settings, config, persisted);
    }

    [Fact]
    public void CameraRows_ContainEveryCameraRow_AndTheStandardRowsDoNot()
    {
        (List<MenuOptionRow> cameraRows, GameSettings settings, DesktopClientConfig config, _) = Create();
        List<MenuOptionRow> standard = MenuOptionRows.CreateStandard(settings, config, () => { }, () => { });
        foreach (string label in Labels)
        {
            Assert.DoesNotContain(standard, r => r.Label == label);
        }

        Assert.Equal(Labels.Length, cameraRows.Count);
        StandardRows_ContainEveryCameraRow();
    }

    private static void StandardRows_ContainEveryCameraRow()
    {
        (List<MenuOptionRow> rows, _, _, _) = Create();
        foreach (string label in Labels)
        {
            Assert.Single(rows, r => r.Label == label);
        }
    }

    [Fact]
    public void SliderRows_AreBounded_AndTheirEndsAreReachable()
    {
        (List<MenuOptionRow> rows, GameSettings settings, DesktopClientConfig config, _) = Create();
        foreach (string label in Labels)
        {
            MenuOptionRow row = rows.Single(r => r.Label == label);
            if (row.Kind != MenuOptionKind.Slider)
            {
                continue;
            }

            Assert.True(row.Max > row.Min, label);
            Assert.True(row.Step > 0f, label);
            foreach (float end in new[] { row.Min, row.Max })
            {
                row.SetValue!(end);
                Assert.Equal(end, row.GetValue!(), 2);
                Assert.False(string.IsNullOrEmpty(row.FormatValue(end)), label);
            }

            // A slider's range is exactly what Sanitized accepts: the stored value never moved.
            Assert.Equal(settings.TacticalCamera, settings.TacticalCamera.Sanitized());
            Assert.Equal(config.TacticalCamera, settings.TacticalCamera);
        }
    }

    [Fact]
    public void Toggles_WriteSettingsAndConfig()
    {
        (List<MenuOptionRow> rows, GameSettings settings, DesktopClientConfig config, int[] persisted) = Create();

        rows.Single(r => r.Label == "Edge Pan").SetToggle!(false);
        rows.Single(r => r.Label == "Free Camera (Y)").SetToggle!(true);
        rows.Single(r => r.Label == "Snap On Release").SetToggle!(true);
        rows.Single(r => r.Label == "Invert Zoom").SetToggle!(true);

        Assert.False(settings.TacticalCamera.EdgePanEnabled);
        Assert.Equal(CameraAnchorMode.Free, settings.TacticalCamera.AnchorMode);
        Assert.True(settings.TacticalCamera.SnapOnDragRelease);
        Assert.True(settings.TacticalCamera.InvertZoom);
        Assert.Equal(settings.TacticalCamera, config.TacticalCamera);

        rows.Single(r => r.Label == "Invert Zoom").Commit!();
        Assert.Equal(1, persisted[0]);
    }

    [Fact]
    public void Sliders_MapToTheirSettings()
    {
        (List<MenuOptionRow> rows, GameSettings settings, DesktopClientConfig config, _) = Create();

        rows.Single(r => r.Label == "Cursor Confine").SetValue!(2f);
        Assert.Equal(CameraConfineMode.Always, settings.TacticalCamera.ConfineMode);
        Assert.Equal("Always", rows.Single(r => r.Label == "Cursor Confine").FormatValue(2f));

        rows.Single(r => r.Label == "Rotate Snap Step").SetValue!(1f);
        Assert.Equal(30f, settings.TacticalCamera.RotateSnapStepDegrees);
        Assert.Equal(1f, rows.Single(r => r.Label == "Rotate Snap Step").GetValue!());

        rows.Single(r => r.Label == "Return To Player").SetValue!(0f);
        Assert.Equal("Off", rows.Single(r => r.Label == "Return To Player").FormatValue(0f));
        rows.Single(r => r.Label == "Return To Player").SetValue!(4f);
        Assert.Equal(4f, settings.TacticalCamera.ReturnToPlayerSeconds);

        rows.Single(r => r.Label == "Pan Speed").SetValue!(30f);
        Assert.Equal(30f, config.TacticalCamera.PanSpeed);
    }

    [Fact]
    public void ExistingRows_AreStillThere()
    {
        (_, GameSettings settings, DesktopClientConfig config, _) = Create();
        List<MenuOptionRow> rows = MenuOptionRows.CreateStandard(settings, config, () => { }, () => { });
        Assert.Contains(rows, r => r.Label == "UI Scale");
        Assert.Contains(rows, r => r.Label == "Max FPS");
        Assert.Contains(rows, r => r.Label == "VSync");
    }
}
