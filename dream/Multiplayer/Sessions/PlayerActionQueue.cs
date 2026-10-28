using Dream.Multiplayer.Protocol;

namespace Dream.Multiplayer.Sessions;

public sealed class PlayerActionQueue : IPlayerActionQueue
{
    private readonly object _sync = new();
    private readonly List<QueuedPlayerAction> _pending = [];
    private ulong _nextReceiveOrder;

    // Most actions one session may have waiting for a tick. Further actions are rejected until a
    // tick drains the session's backlog.
    public const int MaxPendingActionsPerSession = 16;

    // Throws when the session already has MaxPendingActionsPerSession actions pending; use
    // TryEnqueue to handle that without an exception.
    public QueuedPlayerAction Enqueue(PlayerActionMessage message)
    {
        if (!TryEnqueue(message, out QueuedPlayerAction queued))
        {
            throw new InvalidOperationException(
                $"Session '{message.SessionId.Value}' already has {MaxPendingActionsPerSession} pending actions.");
        }

        return queued;
    }

    public bool TryEnqueue(PlayerActionMessage message, out QueuedPlayerAction queued)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_sync)
        {
            int pendingForSession = 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].SessionId == message.SessionId)
                {
                    pendingForSession++;
                }
            }

            if (pendingForSession >= MaxPendingActionsPerSession)
            {
                queued = default;
                return false;
            }

            queued = new QueuedPlayerAction(
                ++_nextReceiveOrder,
                message.SessionId,
                message);
            _pending.Add(queued);
            return true;
        }
    }

    // Drops every pending action of a session, for example when its connection is released.
    // Returns how many were dropped.
    public int RemoveSession(PlayerSessionId sessionId)
    {
        lock (_sync)
        {
            int kept = 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                QueuedPlayerAction action = _pending[i];
                if (action.SessionId != sessionId)
                {
                    _pending[kept++] = action;
                }
            }

            int removed = _pending.Count - kept;
            _pending.RemoveRange(kept, removed);
            return removed;
        }
    }

    public IReadOnlyList<QueuedPlayerAction> Drain(IEnumerable<PlayerSessionId> joinedSessionIds)
    {
        ArgumentNullException.ThrowIfNull(joinedSessionIds);

        PlayerSessionId[] joined = joinedSessionIds.ToArray();
        List<QueuedPlayerAction> drained = [];
        DrainInto(joined, drained);
        return drained.ToArray();
    }

    // Replaces the destination's contents with the joined sessions' pending actions, ordered by
    // receive order, then session id (ordinal), then action sequence. Unjoined actions stay pending
    // until the session joins or RemoveSession drops them.
    public void DrainInto(IReadOnlyList<PlayerSessionId> joinedSessionIds, List<QueuedPlayerAction> destination)
    {
        ArgumentNullException.ThrowIfNull(joinedSessionIds);
        ArgumentNullException.ThrowIfNull(destination);

        destination.Clear();
        lock (_sync)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            int kept = 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                QueuedPlayerAction action = _pending[i];
                if (Contains(joinedSessionIds, action.SessionId))
                {
                    destination.Add(action);
                }
                else
                {
                    _pending[kept++] = action;
                }
            }

            _pending.RemoveRange(kept, _pending.Count - kept);
        }

        SortStable(destination);
    }

    private static bool Contains(IReadOnlyList<PlayerSessionId> sessionIds, PlayerSessionId sessionId)
    {
        for (int i = 0; i < sessionIds.Count; i++)
        {
            if (sessionIds[i] == sessionId)
            {
                return true;
            }
        }

        return false;
    }

    // Insertion sort: stable like the OrderBy/ThenBy chain it replaces, allocation-free, and linear
    // on the usual input, which is already in receive order.
    private static void SortStable(List<QueuedPlayerAction> actions)
    {
        for (int i = 1; i < actions.Count; i++)
        {
            QueuedPlayerAction current = actions[i];
            int j = i - 1;
            while (j >= 0 && DrainOrderComparer.Instance.Compare(actions[j], current) > 0)
            {
                actions[j + 1] = actions[j];
                j--;
            }

            actions[j + 1] = current;
        }
    }

    private sealed class DrainOrderComparer : IComparer<QueuedPlayerAction>
    {
        public static readonly DrainOrderComparer Instance = new();

        public int Compare(QueuedPlayerAction x, QueuedPlayerAction y)
        {
            int byReceiveOrder = x.ReceiveOrder.CompareTo(y.ReceiveOrder);
            if (byReceiveOrder != 0)
            {
                return byReceiveOrder;
            }

            int bySession = StringComparer.Ordinal.Compare(x.SessionId.Value, y.SessionId.Value);
            if (bySession != 0)
            {
                return bySession;
            }

            return x.Message.ActionSequence.CompareTo(y.Message.ActionSequence);
        }
    }
}
