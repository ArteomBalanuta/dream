using Dream.Configuration;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Application;

// Where the window sits for one mode. Pure data: Game applies it to the GLFW window.
public readonly record struct WindowPlacement(WindowBorder Border, WindowState State, Vector2i ClientLocation, Vector2i ClientSize);

// The window-mode decisions with no GLFW calls: what placement a mode means on a monitor, what F11 does next, and
// whether a key press is a mode toggle.
public static class WindowModeController
{
    public static WindowPlacement Resolve(
        WindowMode mode,
        Vector2i monitorPosition,
        Vector2i monitorResolution,
        WindowPlacement windowed)
    {
        WindowPlacement restored = windowed with { Border = WindowBorder.Resizable, State = WindowState.Normal };
        if (mode == WindowMode.Windowed || monitorResolution.X <= 0 || monitorResolution.Y <= 0)
        {
            return restored;
        }

        return new WindowPlacement(
            WindowBorder.Hidden,
            mode == WindowMode.Fullscreen ? WindowState.Fullscreen : WindowState.Normal,
            monitorPosition,
            monitorResolution);
    }

    public static WindowMode Toggle(WindowMode current, WindowMode lastFullscreen)
    {
        if (current != WindowMode.Windowed)
        {
            return WindowMode.Windowed;
        }

        return lastFullscreen == WindowMode.Windowed ? WindowMode.Borderless : lastFullscreen;
    }

    public static bool IsFullscreenLike(WindowMode mode) => mode != WindowMode.Windowed;

    // F11, or Alt+Enter on either Enter key; held keys do not repeat the toggle, and Ctrl+Alt (AltGr) is not Alt.
    public static bool IsWindowModeToggle(Keys key, KeyModifiers modifiers, bool isRepeat)
    {
        if (isRepeat)
        {
            return false;
        }

        if (key == Keys.F11)
        {
            return true;
        }

        return (key == Keys.Enter || key == Keys.KeyPadEnter)
            && (modifiers & KeyModifiers.Alt) != 0
            && (modifiers & KeyModifiers.Control) == 0;
    }

    // Alt-tab out of exclusive fullscreen can leave WindowState reading Minimized/Normal while GLFW still holds the
    // window on a monitor; either signal means the monitor must be released before resizing.
    public static bool IsExclusive(bool glfwHasMonitor, WindowMode current) =>
        glfwHasMonitor || current == WindowMode.Fullscreen;

    // Iconifying exclusive fullscreen reports a 0x0 client; that is never a size to build targets for.
    public static bool IsUsableSize(Vector2i size) => size.X > 0 && size.Y > 0;

    // Resolution presets set the client size directly, which only makes sense for a windowed window.
    public static bool CanSetResolution(WindowMode current) => current == WindowMode.Windowed;

    // Capture runs need the fixed client size, so they never change the window mode.
    public static bool ShouldApply(Func<string, string?> getEnvironmentVariable, string menuCaptureVariable) =>
        string.IsNullOrWhiteSpace(getEnvironmentVariable(Dream.Client.Menu.MenuLaunchPolicy.HudCaptureEnvironmentVariable))
        && string.IsNullOrWhiteSpace(getEnvironmentVariable(menuCaptureVariable));
}

// The current mode, the fullscreen mode F11 returns to, and the windowed placement to restore.
public sealed class WindowModeState
{
    public WindowMode Current { get; private set; } = WindowMode.Windowed;

    public WindowMode LastFullscreen { get; private set; } = WindowMode.Borderless;

    public WindowPlacement Windowed { get; private set; }

    public WindowModeState(WindowPlacement windowed) => Windowed = windowed;

    // Moves to a mode. liveWindowed is where the window is right now; it is remembered only when leaving Windowed.
    // Returns the placement to apply.
    public WindowPlacement Enter(WindowMode mode, WindowPlacement liveWindowed, Vector2i monitorPosition, Vector2i monitorResolution)
    {
        if (Current == WindowMode.Windowed && mode != WindowMode.Windowed)
        {
            Windowed = liveWindowed with { Border = WindowBorder.Resizable, State = WindowState.Normal };
        }

        if (mode != WindowMode.Windowed)
        {
            LastFullscreen = mode;
        }

        Current = mode;
        return WindowModeController.Resolve(mode, monitorPosition, monitorResolution, Windowed);
    }
}
