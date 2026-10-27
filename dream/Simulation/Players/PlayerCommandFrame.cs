using Dream.Multiplayer.Sessions;

namespace Dream.Simulation.Players;

public readonly record struct PlayerCommandFrame(
    PlayerSessionId SessionId,
    ulong Sequence,
    PlayerModeSwitchIntent RequestedMode,
    PlayerMoveIntent MoveIntent,
    PlayerLookIntent LookIntent,
    bool PrimaryUsePressed)
{
    // No movement, look, mode request or primary use.
    public static PlayerCommandFrame CreateIdle(PlayerSessionId sessionId, ulong sequence)
    {
        return new PlayerCommandFrame(
            sessionId,
            sequence,
            PlayerModeSwitchIntent.None,
            PlayerMoveIntent.None,
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
    }

    public PlayerInteractionCommand ToInteractionCommand()
    {
        return new PlayerInteractionCommand(
            PrimaryUsePressed,
            SelectBuildingModePressed: RequestedMode.Mode == PlayerInteractionMode.Building,
            SelectCombatModePressed: RequestedMode.Mode == PlayerInteractionMode.Combat);
    }
}
