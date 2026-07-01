public sealed class ClientScenePresenter
{
    private string? _localSessionId;
    private readonly Dictionary<int, RemotePlayerEntry> _remotePlayers = [];
    private readonly Dictionary<string, PlayerRosterEntry> _playerRoster = new(StringComparer.Ordinal);

    public ClientScenePresenter(string? localSessionId = null)
    {
        _localSessionId = localSessionId;
    }

    public void ApplySnapshot(ServerSnapshotEnvelope snapshot, double receivedAtSeconds)
    {
        HashSet<int> observedRemoteActorIds = [];
        HashSet<string> observedPlayerSessions = new(StringComparer.Ordinal);
        foreach (ActorSnapshot actor in snapshot.Actors)
        {
            if (actor.Kind != CreatureKind.Player || string.IsNullOrWhiteSpace(actor.SessionId))
            {
                continue;
            }

            observedPlayerSessions.Add(actor.SessionId);
            string displayName = string.IsNullOrWhiteSpace(actor.DisplayName)
                ? actor.SessionId
                : actor.DisplayName;
            _playerRoster[actor.SessionId] = new PlayerRosterEntry(actor.SessionId, actor.ActorId, displayName);

            if (string.Equals(actor.SessionId, _localSessionId, StringComparison.Ordinal))
            {
                continue;
            }

            observedRemoteActorIds.Add(actor.ActorId);
            RemotePlayerEntry entry = GetOrCreateEntry(actor.ActorId, actor.SessionId);
            entry.DisplayName = displayName;
            entry.CurrentHealth = actor.CurrentHealth;
            entry.MaxHealth = actor.MaxHealth;
            entry.IsAlive = actor.IsAlive;
            entry.IsMoving = entry.LastSnapshotPosition.HasValue &&
                (actor.Position - entry.LastSnapshotPosition.Value).LengthSquared > 0.0001f;
            entry.LastSnapshotPosition = actor.Position;
            entry.Buffer.AddSample(new RemotePlayerSnapshotSample(
                actor.ActorId,
                actor.SessionId,
                snapshot.ServerTick,
                actor.Position,
                actor.Facing,
                receivedAtSeconds));
        }

        foreach ((int actorId, RemotePlayerEntry entry) in _remotePlayers.ToArray())
        {
            if (!observedRemoteActorIds.Contains(actorId))
            {
                _remotePlayers.Remove(actorId);
            }
        }

        foreach (string sessionId in _playerRoster.Keys.ToArray())
        {
            if (string.Equals(sessionId, _localSessionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!observedPlayerSessions.Contains(sessionId))
            {
                _playerRoster.Remove(sessionId);
            }
        }
    }

    public void ApplyJoinAccepted(JoinAcceptedMessage joinAccepted)
    {
        if (!string.IsNullOrWhiteSpace(joinAccepted.SessionId))
        {
            _localSessionId = joinAccepted.SessionId;
        }

        foreach (PlayerRosterEntry entry in joinAccepted.EffectiveRoster)
        {
            string displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
                ? entry.SessionId
                : entry.DisplayName;
            _playerRoster[entry.SessionId] = new PlayerRosterEntry(entry.SessionId, entry.ActorId, displayName);
        }
    }

    public IReadOnlyList<PlayerRosterEntry> BuildPlayerRoster()
    {
        return _playerRoster.Values
            .OrderByDescending(entry => string.Equals(entry.SessionId, _localSessionId, StringComparison.Ordinal))
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<RemotePlayerPresentationState> BuildRemotePlayerStates(double renderTimeSeconds)
    {
        List<RemotePlayerPresentationState> states = [];
        foreach ((int actorId, RemotePlayerEntry entry) in _remotePlayers)
        {
            if (!entry.Buffer.TrySample(renderTimeSeconds, out InterpolatedActorPose pose))
            {
                continue;
            }

            states.Add(new RemotePlayerPresentationState(
                actorId,
                entry.SessionId,
                entry.DisplayName,
                pose,
                entry.CurrentHealth,
                entry.MaxHealth,
                entry.IsAlive,
                entry.IsMoving));
        }

        return states;
    }

    private RemotePlayerEntry GetOrCreateEntry(int actorId, string sessionId)
    {
        if (_remotePlayers.TryGetValue(actorId, out RemotePlayerEntry? entry))
        {
            return entry;
        }

        RemotePlayerEntry created = new(sessionId);
        _remotePlayers[actorId] = created;
        return created;
    }

    private sealed class RemotePlayerEntry(string sessionId)
    {
        public string SessionId { get; } = sessionId;

        public string DisplayName { get; set; } = sessionId;

        public RemotePlayerInterpolationBuffer Buffer { get; } = new();

        public int CurrentHealth { get; set; }

        public int MaxHealth { get; set; }

        public bool IsAlive { get; set; }

        public bool IsMoving { get; set; }

        public OpenTK.Mathematics.Vector3? LastSnapshotPosition { get; set; }
    }
}
