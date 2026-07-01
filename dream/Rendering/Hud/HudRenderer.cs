using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Dream.Application;
using Dream.Client.Presentation;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Core;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Rendering.Hud;


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
