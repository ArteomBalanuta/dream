using System.Collections;

namespace Dream.Multiplayer.Sessions;

// Every session the server has seen, split into the active ones (spawned remote players and the
// local player) and the ones that left (SCALE-7).
//
// A client picks a fresh session id on every launch, so a long-running server sees an unbounded
// number of session ids. The tick walks Players (accepted command sequences, checkpoints), so
// Players holds only the active sessions: the walk costs what the connected players cost, however
// many sessions came and went before.
//
// A session that leaves (ConnectedPlayer.MarkDespawned) keeps its entry for a while, because a
// returning session (same session id) reattaches through it: AttachRemoteSession reclaims the
// actor id the entry still carries when no one took it meanwhile (wave 10), re-seats a returning
// former primary and keeps its display name. Those entries form a bounded LRU of the most recently
// left sessions, MaxDepartedSessions long: past it, the session that left longest ago is
// forgotten. A forgotten session that returns joins like a new one: its persisted profile is
// restored by player name as for any join, and it takes an id from the released-id pool (its old
// id went back to that pool when it left, so ids stay bounded either way).
//
// State other layers keep per session id (WorldSessionRuntime's interaction modes) must follow
// the registry, or it grows by one entry per connect while the registry stays bounded: such an
// owner sets SessionForgotten and drops the session's state there.
public sealed class PlayerSessionRegistry
{
    // How many sessions that left are remembered for a reattach. Large enough to cover every
    // player of a busy server reconnecting at once; small enough that the table stays a few
    // hundred kilobytes at most whatever the uptime.
    public const int DefaultMaxDepartedSessions = 1024;

    private readonly Dictionary<PlayerSessionId, ConnectedPlayer> _players = new();

    // Active sessions in first-registration order (the order the old single dictionary iterated
    // in), so the tick walks and the checkpoint keep their order. The order holds only among
    // remembered sessions: a session the registry forgot and that comes back registers anew, so it
    // goes after every session registered before its return.
    private readonly List<ConnectedPlayer> _active = [];

    // Sessions that left, oldest departure first; the nodes live on the players and are reused.
    private readonly LinkedList<ConnectedPlayer> _departed = new();

    private readonly ConnectedPlayerView _activeView;
    private long _nextRegistrationOrder;
    private int _spawnedRemoteCount;

    public PlayerSessionRegistry(int maxDepartedSessions = DefaultMaxDepartedSessions)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepartedSessions);
        MaxDepartedSessions = maxDepartedSessions;
        _activeView = new ConnectedPlayerView(_active);
    }

    public int MaxDepartedSessions { get; }

    // Called when the registry drops an entry: a departed session evicted from the LRU
    // (TrimDeparted), or an entry replaced by Register under the same session id (the replaced
    // object is passed; the id is registered again right after the call returns). One owner sets
    // it: WorldSessionRuntime, which drops the session's interaction mode. Runs on the thread
    // that drives the registry (the server tick), inside Register or a spawn-state change, so it
    // must not touch the registry.
    internal Action<ConnectedPlayer>? SessionForgotten { get; set; }

    // The active sessions: spawned remote players and the local player. A foreach uses the list's
    // struct enumerator, so the tick walk does not allocate. Sessions that left are not listed;
    // TryGet still finds the ones the registry remembers. The view is live: registering a session
    // or changing one's spawn state while enumerating it throws InvalidOperationException (the
    // list enumerator's version check), so a walk that may do either copies the view first.
    public ConnectedPlayerView Players => _activeView;

    // Every remembered session, active or departed.
    public int Count => _players.Count;

    public int ActiveCount => _active.Count;

    public int DepartedCount => _departed.Count;

    public bool HasSpawnedRemotePlayer()
    {
        return _spawnedRemoteCount > 0;
    }

    public ConnectedPlayer Register(
        PlayerSessionId sessionId,
        int actorId,
        bool isLocal,
        string? displayName = null,
        bool isSpawned = true)
    {
        ConnectedPlayer player = new(sessionId, actorId, isLocal, displayName ?? sessionId.Value, isSpawned);
        if (_players.Remove(sessionId, out ConnectedPlayer? replaced))
        {
            // Re-registering a session id (a returning former primary given its own seat) keeps
            // its place in the walk order.
            player.RegistrationOrder = replaced.RegistrationOrder;
            Forget(replaced);
        }
        else
        {
            player.RegistrationOrder = _nextRegistrationOrder++;
        }

        _players[sessionId] = player;
        player.Registry = this;
        Track(player);
        TrimDeparted();
        return player;
    }

    public bool TryGet(PlayerSessionId sessionId, out ConnectedPlayer? player)
    {
        return _players.TryGetValue(sessionId, out player);
    }

    public bool TryAcceptCommand(PlayerSessionId sessionId, ulong sequence, out ConnectedPlayer? player)
    {
        if (!_players.TryGetValue(sessionId, out player))
        {
            return false;
        }

        if (!player.IsSpawned)
        {
            return false;
        }

        return player.TryAcceptSequence(sequence);
    }

    public bool TryAcceptAction(PlayerSessionId sessionId, ulong sequence, out ConnectedPlayer? player)
    {
        if (!_players.TryGetValue(sessionId, out player))
        {
            return false;
        }

        if (!player.IsSpawned)
        {
            return false;
        }

        return player.TryAcceptActionSequence(sequence);
    }

    // ConnectedPlayer.MarkSpawned / MarkDespawned report the change here.
    internal void OnSpawnStateChanged(ConnectedPlayer player)
    {
        if (!ReferenceEquals(player.Registry, this))
        {
            return;
        }

        Untrack(player);
        Track(player);
        TrimDeparted();
    }

    private void Track(ConnectedPlayer player)
    {
        if (player.IsSpawned || player.IsLocal)
        {
            InsertActive(player);
            if (player.IsSpawned && !player.IsLocal)
            {
                _spawnedRemoteCount++;
            }
        }
        else
        {
            player.DepartedNode ??= new LinkedListNode<ConnectedPlayer>(player);
            _departed.AddLast(player.DepartedNode);
        }
    }

    private void Untrack(ConnectedPlayer player)
    {
        if (player.DepartedNode?.List != null)
        {
            _departed.Remove(player.DepartedNode);
            return;
        }

        int index = FindActiveIndex(player);
        if (index >= 0)
        {
            _active.RemoveAt(index);
            if (player.CountedAsSpawnedRemote)
            {
                _spawnedRemoteCount--;
            }
        }
    }

    private void Forget(ConnectedPlayer player)
    {
        Untrack(player);
        player.Registry = null;
        SessionForgotten?.Invoke(player);
    }

    private void TrimDeparted()
    {
        while (_departed.Count > MaxDepartedSessions)
        {
            ConnectedPlayer oldest = _departed.First!.Value;
            _departed.RemoveFirst();
            _players.Remove(oldest.SessionId);
            oldest.Registry = null;
            SessionForgotten?.Invoke(oldest);
        }
    }

    private void InsertActive(ConnectedPlayer player)
    {
        player.CountedAsSpawnedRemote = player.IsSpawned && !player.IsLocal;
        // A newly registered session is the newest, so this is an append; a returning one goes
        // back to its first-registration place.
        int index = _active.Count;
        while (index > 0 && _active[index - 1].RegistrationOrder > player.RegistrationOrder)
        {
            index--;
        }

        _active.Insert(index, player);
    }

    private int FindActiveIndex(ConnectedPlayer player)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_active[i], player))
            {
                return i;
            }
        }

        return -1;
    }

    // A read-only view of the active sessions. foreach binds to the struct enumerator; LINQ and
    // other IEnumerable callers go through the interfaces.
    public sealed class ConnectedPlayerView : IReadOnlyList<ConnectedPlayer>
    {
        private readonly List<ConnectedPlayer> _players;

        internal ConnectedPlayerView(List<ConnectedPlayer> players)
        {
            _players = players;
        }

        public int Count => _players.Count;

        public ConnectedPlayer this[int index] => _players[index];

        public List<ConnectedPlayer>.Enumerator GetEnumerator() => _players.GetEnumerator();

        IEnumerator<ConnectedPlayer> IEnumerable<ConnectedPlayer>.GetEnumerator() => _players.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _players.GetEnumerator();
    }
}
