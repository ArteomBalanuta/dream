using Dream.Simulation.Players;

namespace Dream.Multiplayer.Sessions;

public interface IPlayerCommandInbox
{
    void Enqueue(PlayerCommandFrame commandFrame);

    IReadOnlyList<PlayerCommandFrame> Drain(IEnumerable<PlayerSessionId> sessionIds);
}
