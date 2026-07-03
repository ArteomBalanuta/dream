using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Dream.Application;
using Dream.Client.Presentation;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Core;
using Dream.Simulation.Inventory;
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
        PlayerInventorySnapshot? inventory = null,
        string? connectionLabel = null,
        FramePerformanceSnapshot performance = default)
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
            inventory,
            connectionLabel,
            performance);
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
        PlayerInventorySnapshot? inventory = null,
        string? connectionLabel = null,
        FramePerformanceSnapshot performance = default)
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
            inventory,
            connectionLabel,
            performance);
    }

    public void UpdateProjectionForSettings(GameSettings settings)
    {
        _render.UpdateProjectionForSettings(settings);
    }
}
