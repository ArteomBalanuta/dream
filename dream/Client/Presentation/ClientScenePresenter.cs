using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Client.Presentation;

public sealed class ClientScenePresenter
{
    private string? _localSessionId;
    private readonly Dictionary<int, RemotePlayerEntry> _remotePlayers = [];
    private readonly Dictionary<string, PlayerRosterEntry> _playerRoster = new(StringComparer.Ordinal);

    public ClientScenePresenter(string? localSessionId = null)
    {
        _localSessionId = localSessionId;
    }

    public void ApplySnapshot(ServerSnapshotEnvelope snapshot)
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
                actor.Facing));
        }

        foreach ((int actorId, RemotePlayerEntry entry) in _remotePlayers.ToArray())
        {
            if (!observedRemoteActorIds.Contains(actorId))
            {
                _remotePlayers.Remove(actorId);
                NetworkDiagnosticsLog.WriteEvery(
                    $"client.presenter.prune.{actorId}",
                    TimeSpan.FromSeconds(1),
                    "client.presenter.prune",
                    () => $"local={_localSessionId ?? "<none>"} actor={actorId} session={entry.SessionId} snapshotTick={snapshot.ServerTick} " +
                        NetworkDiagnosticsLog.DescribeSnapshot(snapshot));
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

        NetworkDiagnosticsLog.WriteEvery(
            $"client.presenter.apply.{_localSessionId ?? "none"}",
            TimeSpan.FromSeconds(1),
            "client.presenter.apply",
            () => $"local={_localSessionId ?? "<none>"} remoteEntries={_remotePlayers.Count} roster={DescribeRosterSessions()} " +
                NetworkDiagnosticsLog.DescribeSnapshot(snapshot));
    }

    public void ApplyJoinAccepted(JoinAcceptedMessage joinAccepted)
    {
        if (!string.IsNullOrWhiteSpace(joinAccepted.SessionId))
        {
            _localSessionId = joinAccepted.SessionId;
        }

        // A join starts a new server tick timeline; samples keyed by the old one would never be replaced.
        _remotePlayers.Clear();

        foreach (PlayerRosterEntry entry in joinAccepted.EffectiveRoster)
        {
            string displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
                ? entry.SessionId
                : entry.DisplayName;
            _playerRoster[entry.SessionId] = new PlayerRosterEntry(entry.SessionId, entry.ActorId, displayName);
        }

        if (joinAccepted.EffectiveInitialActors.Count > 0)
        {
            ApplySnapshot(new ServerSnapshotEnvelope(
                joinAccepted.CurrentServerTick,
                new Dictionary<string, ulong>(),
                joinAccepted.EffectiveInitialActors,
                PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>()));
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

    public IReadOnlyList<RemotePlayerPresentationState> BuildRemotePlayerStates(double renderTick)
    {
        List<RemotePlayerPresentationState> states = [];
        foreach ((int actorId, RemotePlayerEntry entry) in _remotePlayers)
        {
            if (!entry.Buffer.TrySample(renderTick, out InterpolatedActorPose pose))
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

    public ClientScenePresenterDebugState CaptureDebugState(double renderTick)
    {
        List<string> remoteSessions = [];
        int sampledRemoteCount = 0;
        foreach ((int actorId, RemotePlayerEntry entry) in _remotePlayers)
        {
            remoteSessions.Add($"{entry.SessionId}#{actorId}/{entry.DisplayName}/alive={entry.IsAlive}");
            if (entry.Buffer.TrySample(renderTick, out _))
            {
                sampledRemoteCount++;
            }
        }

        return new ClientScenePresenterDebugState(
            _localSessionId,
            _playerRoster.Count,
            _playerRoster.Values
                .Select(entry => $"{entry.SessionId}#{entry.ActorId}/{entry.DisplayName}")
                .OrderBy(entry => entry, StringComparer.Ordinal)
                .ToArray(),
            _remotePlayers.Count,
            remoteSessions.OrderBy(entry => entry, StringComparer.Ordinal).ToArray(),
            sampledRemoteCount);
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

    private string DescribeRosterSessions()
    {
        return $"[{string.Join(", ", _playerRoster.Values.Select(entry => $"{entry.SessionId}#{entry.ActorId}/{entry.DisplayName}"))}]";
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
