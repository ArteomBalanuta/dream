using Dream.Multiplayer.Protocol;

namespace Dream.Multiplayer.Sessions;

public interface IPlayerActionQueue
{
    QueuedPlayerAction Enqueue(PlayerActionMessage message);

    IReadOnlyList<QueuedPlayerAction> Drain(IEnumerable<PlayerSessionId> joinedSessionIds);
}
