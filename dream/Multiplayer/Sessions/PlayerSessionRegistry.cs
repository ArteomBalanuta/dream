namespace Dream.Multiplayer.Sessions;

public sealed class PlayerSessionRegistry
{
    private readonly Dictionary<PlayerSessionId, ConnectedPlayer> _players = new();

    // The concrete value collection, so a foreach on the tick path uses its struct enumerator
    // instead of boxing one through IEnumerable. It is still a read-only view.
    public Dictionary<PlayerSessionId, ConnectedPlayer>.ValueCollection Players => _players.Values;

    public bool HasSpawnedRemotePlayer()
    {
        foreach (ConnectedPlayer player in _players.Values)
        {
            if (!player.IsLocal && player.IsSpawned)
            {
                return true;
            }
        }

        return false;
    }

    public ConnectedPlayer Register(
        PlayerSessionId sessionId,
        int actorId,
        bool isLocal,
        string? displayName = null,
        bool isSpawned = true)
    {
        ConnectedPlayer player = new(sessionId, actorId, isLocal, displayName ?? sessionId.Value, isSpawned);
        _players[sessionId] = player;
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
}
