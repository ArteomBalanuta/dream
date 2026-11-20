using System.IO;
using OpenTK.Mathematics;
using Xunit;
using Dream.Application;
using Dream.Client.Menu;
using Dream.Configuration;
using Dream.Rendering.Ui.Menu;

namespace Dream.Tests.Client.Menu;

public class WindowModeTests
{
    private static DesktopClientConfig Load(string text)
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "desktop-client.txt");
        File.WriteAllText(path, text);
        return DesktopClientTextConfig.LoadOrCreate(path);
    }

    [Theory]
    [InlineData(WindowMode.Windowed, "window_mode=windowed")]
    [InlineData(WindowMode.Borderless, "window_mode=borderless")]
    [InlineData(WindowMode.Fullscreen, "window_mode=fullscreen")]
    public void WindowMode_RoundTrips(WindowMode mode, string line)
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "desktop-client.txt");
        DesktopClientTextConfig.Save(path, new DesktopClientConfig { WindowMode = mode });

        Assert.Contains(line, File.ReadAllLines(path));
        Assert.Equal(mode, DesktopClientTextConfig.LoadOrCreate(path).WindowMode);
    }

    [Theory]
    [InlineData("window_mode=BORDERLESS", WindowMode.Borderless)]
    [InlineData("window_mode=  Fullscreen  ", WindowMode.Fullscreen)]
    [InlineData("window_mode=bogus", WindowMode.Windowed)]
    [InlineData("window_mode=", WindowMode.Windowed)]
    [InlineData("max_fps=120", WindowMode.Windowed)]
    public void WindowMode_ParsesCaseWhitespaceUnknownAndMissing(string text, WindowMode expected)
    {
        Assert.Equal(expected, Load(text + Environment.NewLine).WindowMode);
    }

    private static (List<MenuOptionRow> Rows, DesktopClientConfig Config) Rows(Action? onChanged, Action persist)
    {
        GameSettings settings = new(new Vector2i(1440, 1024));
        DesktopClientConfig config = new();
        return (MenuOptionRows.CreateStandard(settings, config, () => { }, persist, onChanged), config);
    }

    [Fact]
    public void DisplayModeRow_FollowsUiScale()
    {
        (List<MenuOptionRow> rows, _) = Rows(null, () => { });
        int scale = rows.FindIndex(r => r.Label == "UI Scale");
        Assert.Equal("Display Mode", rows[scale + 1].Label);
    }

    [Fact]
    public void DisplayModeRow_StepsWritesConfigCallsBackAndPersists()
    {
        int changed = 0;
        int persisted = 0;
        (List<MenuOptionRow> rows, DesktopClientConfig config) = Rows(() => changed++, () => persisted++);
        MenuOptionRow row = rows.Single(r => r.Label == "Display Mode");

        Assert.Equal(0f, row.GetValue!());
        Assert.Equal("Windowed", row.FormatValue(0f));
        Assert.Equal("Borderless", row.FormatValue(1f));
        Assert.Equal("Fullscreen", row.FormatValue(2f));

        row.SetValue!(1f);
        Assert.Equal(WindowMode.Borderless, config.WindowMode);
        row.SetValue!(2f);
        Assert.Equal(WindowMode.Fullscreen, config.WindowMode);
        Assert.Equal(0, changed);
        row.Commit!();
        Assert.Equal(1, changed);
        Assert.Equal(1, persisted);
    }

    [Fact]
    public void DisplayModeRow_DragAppliesOnceOnRelease()
    {
        int changed = 0;
        (List<MenuOptionRow> rows, _) = Rows(() => changed++, () => { });
        MenuOptionRow row = rows.Single(r => r.Label == "Display Mode");

        row.SetValue!(1f);
        row.SetValue!(2f);
        row.SetValue!(1f);
        Assert.Equal(0, changed);
        row.Commit!();
        Assert.Equal(1, changed);

        row.SetValue!(1f);
        row.Commit!();
        Assert.Equal(1, changed);
    }

    [Fact]
    public void DisplayModeRow_DragBackToStartDoesNotApply()
    {
        int changed = 0;
        int persisted = 0;
        (List<MenuOptionRow> rows, _) = Rows(() => changed++, () => persisted++);
        MenuOptionRow row = rows.Single(r => r.Label == "Display Mode");

        row.SetValue!(2f);
        row.SetValue!(0f);
        row.Commit!();
        Assert.Equal(0, changed);
        Assert.Equal(1, persisted);
    }

    [Fact]
    public void DisplayModeRow_NullCallbackIsFine_AndGetterReadsConfigEachTime()
    {
        (List<MenuOptionRow> rows, DesktopClientConfig config) = Rows(null, () => { });
        MenuOptionRow row = rows.Single(r => r.Label == "Display Mode");

        row.SetValue!(2f);
        Assert.Equal(WindowMode.Fullscreen, config.WindowMode);
        config.WindowMode = WindowMode.Borderless;
        Assert.Equal(1f, row.GetValue!());
        config.WindowMode = WindowMode.Windowed;
        Assert.Equal(0f, row.GetValue!());
    }
}
