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

    // Whether ImGui draws its own software arrow. Off while a sprite cursor owns the pointer: the classic
    // HUD cursor, or the pause menu's (drawn by the menu renderer over the dimmed world). It stays on over
    // a genuine ImGui window (the Build panel, F3, the developer window), where no sprite cursor is drawn.
    public static bool ImGuiDrawsCursor(
        bool classicCursorActive,
        bool settingsVisible,
        bool pauseMenuVisible,
        bool imGuiWantsMouse) =>
        !pauseMenuVisible && !(classicCursorActive && !settingsVisible && !imGuiWantsMouse);

    // The classic sprite cursor hides the OS one whenever it is drawn: Settings closed and the window
    // focused (an unfocused window keeps an OS cursor, which also lets the user leave it). Anything
    // else keeps the state the gameplay input rules give.
    public static CursorState Decide(
        bool classicCursorActive,
        bool settingsVisible,
        bool windowFocused,
        CursorState gameplayState) =>
        classicCursorActive && !settingsVisible && windowFocused
            ? CursorState.Hidden
            : gameplayState;
}
