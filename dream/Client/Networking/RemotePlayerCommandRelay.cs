using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Client.Networking;


public static class RemotePlayerCommandRelay
{
    public static PlayerCommandFrame CreateRelayCommand(
        PlayerCommandFrame localFrame,
        PlayerSessionId remoteSessionId,
        Vector3 facing)
    {
        return new PlayerCommandFrame(
            remoteSessionId,
            localFrame.Sequence,
            localFrame.RequestedMode,
            localFrame.MoveIntent,
            new PlayerLookIntent(0f, 0f, facing),
            localFrame.PrimaryUsePressed);
    }
}
