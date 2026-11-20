using Dream.Application;
using Dream.Client.Menu;
using Dream.Configuration;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;

namespace Dream.Tests.Application;

public class WindowModeControllerTests
{
    private static readonly WindowPlacement Saved =
        new(WindowBorder.Resizable, WindowState.Normal, new Vector2i(120, 80), new Vector2i(1280, 900));

    private static readonly Vector2i MonitorPos = new(1920, 0);
    private static readonly Vector2i MonitorRes = new(2560, 1440);

    [Fact]
    public void Resolve_windowed_returns_the_saved_placement()
    {
        WindowPlacement p = WindowModeController.Resolve(WindowMode.Windowed, MonitorPos, MonitorRes, Saved);
        Assert.Equal(Saved, p);
    }

    [Fact]
    public void Resolve_borderless_covers_the_monitor_without_a_mode_switch()
    {
        WindowPlacement p = WindowModeController.Resolve(WindowMode.Borderless, MonitorPos, MonitorRes, Saved);
        Assert.Equal(new WindowPlacement(WindowBorder.Hidden, WindowState.Normal, MonitorPos, MonitorRes), p);
    }

    [Fact]
    public void Resolve_fullscreen_uses_the_fullscreen_state_on_the_monitor()
    {
        WindowPlacement p = WindowModeController.Resolve(WindowMode.Fullscreen, MonitorPos, MonitorRes, Saved);
        Assert.Equal(new WindowPlacement(WindowBorder.Hidden, WindowState.Fullscreen, MonitorPos, MonitorRes), p);
    }

    [Theory]
    [InlineData(WindowMode.Borderless)]
    [InlineData(WindowMode.Fullscreen)]
    public void Resolve_with_a_degenerate_monitor_falls_back_to_windowed(WindowMode mode)
    {
        Assert.Equal(Saved, WindowModeController.Resolve(mode, MonitorPos, new Vector2i(0, 1080), Saved));
        Assert.Equal(Saved, WindowModeController.Resolve(mode, MonitorPos, new Vector2i(1920, -1), Saved));
    }

    [Theory]
    [InlineData(WindowMode.Windowed, WindowMode.Windowed, WindowMode.Borderless)]
    [InlineData(WindowMode.Windowed, WindowMode.Borderless, WindowMode.Borderless)]
    [InlineData(WindowMode.Windowed, WindowMode.Fullscreen, WindowMode.Fullscreen)]
    [InlineData(WindowMode.Borderless, WindowMode.Fullscreen, WindowMode.Windowed)]
    [InlineData(WindowMode.Fullscreen, WindowMode.Borderless, WindowMode.Windowed)]
    public void Toggle_table(WindowMode current, WindowMode last, WindowMode expected) =>
        Assert.Equal(expected, WindowModeController.Toggle(current, last));

    [Fact]
    public void IsFullscreenLike_is_true_only_for_the_fullscreen_modes()
    {
        Assert.False(WindowModeController.IsFullscreenLike(WindowMode.Windowed));
        Assert.True(WindowModeController.IsFullscreenLike(WindowMode.Borderless));
        Assert.True(WindowModeController.IsFullscreenLike(WindowMode.Fullscreen));
    }

    [Theory]
    [InlineData(WindowMode.Borderless)]
    [InlineData(WindowMode.Fullscreen)]
    public void State_restores_the_windowed_placement_after_a_round_trip(WindowMode mode)
    {
        WindowModeState state = new(Saved);
        WindowPlacement live = Saved with { ClientLocation = new Vector2i(300, 200), ClientSize = new Vector2i(1000, 700) };

        WindowPlacement full = state.Enter(mode, live, MonitorPos, MonitorRes);
        Assert.Equal(MonitorRes, full.ClientSize);
        Assert.Equal(mode, state.Current);

        // The window is now monitor sized; leaving must not remember that as the windowed placement.
        WindowPlacement back = state.Enter(WindowMode.Windowed, full, MonitorPos, MonitorRes);
        Assert.Equal(live, back);
        Assert.Equal(mode, state.LastFullscreen);
        Assert.Equal(WindowMode.Windowed, state.Current);
    }

    [Fact]
    public void State_remembers_the_last_fullscreen_mode_across_switches()
    {
        WindowModeState state = new(Saved);
        state.Enter(WindowMode.Fullscreen, Saved, MonitorPos, MonitorRes);
        state.Enter(WindowMode.Borderless, Saved, MonitorPos, MonitorRes);
        state.Enter(WindowMode.Windowed, Saved, MonitorPos, MonitorRes);
        Assert.Equal(WindowMode.Borderless, state.LastFullscreen);
        Assert.Equal(Saved, state.Windowed);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "  ", true)]
    [InlineData("1", null, false)]
    [InlineData(null, "dir", false)]
    public void Window_mode_is_applied_only_when_no_capture_variable_is_set(string? hud, string? menu, bool expected)
    {
        string? Env(string name) => name == "DREAM_HUD_CAPTURE" ? hud : name == MenuCapture.EnvironmentVariable ? menu : null;
        Assert.Equal(expected, WindowModeController.ShouldApply(Env, MenuCapture.EnvironmentVariable));
    }

    [Theory]
    [InlineData(Keys.F11, default(KeyModifiers), false, true)]
    [InlineData(Keys.Enter, KeyModifiers.Alt, false, true)]
    [InlineData(Keys.KeyPadEnter, KeyModifiers.Alt, false, true)]
    [InlineData(Keys.Enter, default(KeyModifiers), false, false)]
    [InlineData(Keys.Enter, KeyModifiers.Alt, true, false)]
    [InlineData(Keys.F11, default(KeyModifiers), true, false)]
    [InlineData(Keys.Enter, KeyModifiers.Control, false, false)]
    [InlineData(Keys.Enter, KeyModifiers.Control | KeyModifiers.Alt, false, false)]
    public void Toggle_key_detection(Keys key, KeyModifiers modifiers, bool isRepeat, bool expected) =>
        Assert.Equal(expected, WindowModeController.IsWindowModeToggle(key, modifiers, isRepeat));

    [Theory]
    [InlineData(false, WindowMode.Windowed, false)]
    [InlineData(true, WindowMode.Windowed, true)]
    [InlineData(false, WindowMode.Fullscreen, true)]
    [InlineData(false, WindowMode.Borderless, false)]
    public void Exclusive_follows_glfw_monitor_or_mode(bool hasMonitor, WindowMode mode, bool expected) =>
        Assert.Equal(expected, WindowModeController.IsExclusive(hasMonitor, mode));

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 720, false)]
    [InlineData(1280, 0, false)]
    [InlineData(1280, 720, true)]
    public void Zero_sized_client_is_not_usable(int x, int y, bool expected) =>
        Assert.Equal(expected, WindowModeController.IsUsableSize(new Vector2i(x, y)));

    [Fact]
    public void Resolution_presets_only_apply_windowed()
    {
        Assert.True(WindowModeController.CanSetResolution(WindowMode.Windowed));
        Assert.False(WindowModeController.CanSetResolution(WindowMode.Borderless));
        Assert.False(WindowModeController.CanSetResolution(WindowMode.Fullscreen));
    }
}
