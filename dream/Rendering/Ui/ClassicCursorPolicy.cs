using OpenTK.Windowing.Common;

namespace Dream.Rendering.Ui;

// Which OS cursor state the play scene asks for while the classic HUD may be drawing its own
// sprite cursor. Pure, so the table can be tested without a window.
public static class ClassicCursorPolicy
{
    // True when the classic surface is the one in use and ready to draw: not latched to ImGui, not
    // swapped to ImGui with F3, not forced to ImGui, and past loading.
    public static bool WillDraw(HudSurface current, ClassicHudBackendState? backendState) =>
        current == HudSurface.Classic && backendState == ClassicHudBackendState.Ready;

    // The classic cursor hides the OS one only with the inventory open, Settings closed and the window
    // focused (an unfocused window keeps an OS cursor, which also lets the user leave it). Anything
    // else keeps the state the gameplay input rules give.
    public static CursorState Decide(
        bool classicCursorActive,
        bool inventoryVisible,
        bool settingsVisible,
        bool windowFocused,
        CursorState gameplayState) =>
        classicCursorActive && inventoryVisible && !settingsVisible && windowFocused
            ? CursorState.Hidden
            : gameplayState;
}
