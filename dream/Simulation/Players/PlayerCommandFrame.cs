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
    public PlayerInteractionCommand ToInteractionCommand()
    {
        return new PlayerInteractionCommand(
            PrimaryUsePressed,
            SelectBuildingModePressed: RequestedMode.Mode == PlayerInteractionMode.Building,
            SelectCombatModePressed: RequestedMode.Mode == PlayerInteractionMode.Combat);
    }
}
