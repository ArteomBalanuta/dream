using Dream.Multiplayer.Protocol;

namespace Dream.Multiplayer.Sessions;

public sealed class PlayerActionQueue : IPlayerActionQueue
{
    private readonly object _sync = new();
    private readonly List<QueuedPlayerAction> _pending = [];
    private ulong _nextReceiveOrder;

    public QueuedPlayerAction Enqueue(PlayerActionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_sync)
        {
            QueuedPlayerAction queued = new(
                ++_nextReceiveOrder,
                message.SessionId,
                message);
            _pending.Add(queued);
            return queued;
        }
    }

    public IReadOnlyList<QueuedPlayerAction> Drain(IEnumerable<PlayerSessionId> joinedSessionIds)
    {
        ArgumentNullException.ThrowIfNull(joinedSessionIds);

        HashSet<PlayerSessionId> joined = joinedSessionIds.ToHashSet();
        lock (_sync)
        {
            QueuedPlayerAction[] drained = _pending
                .Where(action => joined.Contains(action.SessionId))
                .OrderBy(action => action.ReceiveOrder)
                .ThenBy(action => action.SessionId.Value, StringComparer.Ordinal)
                .ThenBy(action => action.Message.ActionSequence)
                .ToArray();

            _pending.RemoveAll(action => joined.Contains(action.SessionId));
            return drained;
        }
    }
}
