using Dream.Simulation.Players;

namespace Dream.Client.Replication;

public static class ClientSimulationCommandPolicy
{
    public static PlayerCommandFrame PrepareForLocalSimulation(
        PlayerCommandFrame commandFrame,
        bool useRemoteAuthority)
    {
        if (!useRemoteAuthority)
        {
            return new PlayerCommandFrame(
                commandFrame.SessionId,
                commandFrame.Sequence,
                commandFrame.RequestedMode,
                commandFrame.MoveIntent,
                PlayerLookIntent.None,
                commandFrame.PrimaryUsePressed);
        }

        return new PlayerCommandFrame(
            commandFrame.SessionId,
            commandFrame.Sequence,
            commandFrame.RequestedMode,
            commandFrame.MoveIntent,
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
    }
}
