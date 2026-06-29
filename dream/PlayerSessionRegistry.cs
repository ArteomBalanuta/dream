public sealed class PlayerSessionRegistry
{
    private readonly Dictionary<PlayerSessionId, ConnectedPlayer> _players = new();

    public IReadOnlyCollection<ConnectedPlayer> Players => _players.Values;

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
}
