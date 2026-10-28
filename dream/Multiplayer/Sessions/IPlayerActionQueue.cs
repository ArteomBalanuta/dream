using Dream.Multiplayer.Protocol;

namespace Dream.Multiplayer.Sessions;

public interface IPlayerActionQueue
{
    QueuedPlayerAction Enqueue(PlayerActionMessage message);

    // Queues the action unless the session's pending-action cap is full; false (and nothing
    // queued) when it is.
    bool TryEnqueue(PlayerActionMessage message, out QueuedPlayerAction queued);

    IReadOnlyList<QueuedPlayerAction> Drain(IEnumerable<PlayerSessionId> joinedSessionIds);
}
