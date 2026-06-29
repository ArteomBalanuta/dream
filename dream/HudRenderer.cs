using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

public class HudRenderer
{
    private readonly Render _render;

    public HudRenderer(Render render)
    {
        _render = render;
    }

    public HudFrameResult DrawFrame(
        FrameEventArgs args,
        bool windowFocused,
        GameSettings settings,
        MouseState mouseState,
        Camera camera,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        IReadOnlyList<EnemyActor> activeEnemies,
        IReadOnlyList<PlayerRosterEntry> playerRoster,
        CombatFeedbackSystem combatFeedback,
        string? rejectionMessage,
        HudStatusSnapshot status,
        string? connectionLabel = null)
    {
        return _render.DrawHUD(
            args,
            windowFocused,
            settings,
            mouseState,
            camera,
            remotePlayers,
            activeEnemies,
            playerRoster,
            combatFeedback,
            rejectionMessage,
            status,
            connectionLabel);
    }

    public HudFrameResult DrawFrame(
        FrameEventArgs args,
        bool windowFocused,
        GameSettings settings,
        MouseState mouseState,
        WorldSessionPresentationState state,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        IReadOnlyList<PlayerRosterEntry> playerRoster,
        CombatFeedbackSystem combatFeedback,
        string? rejectionMessage,
        string? connectionLabel = null)
    {
        return DrawFrame(
            args,
            windowFocused,
            settings,
            mouseState,
            state.Camera,
            remotePlayers,
            state.ActiveEnemies,
            playerRoster,
            combatFeedback,
            rejectionMessage,
            state.HudStatus,
            connectionLabel);
    }

    public void UpdateProjectionForSettings(GameSettings settings)
    {
        _render.UpdateProjectionForSettings(settings);
    }
}
