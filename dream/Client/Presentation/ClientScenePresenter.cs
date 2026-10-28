using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using OpenTK.Mathematics;

namespace Dream.Client.Presentation;

// Runs on every render frame (BuildRemotePlayerStates, BuildPlayerRoster) and on every snapshot
// (ApplySnapshot), so steady state allocates nothing: scratch collections are reused and the
// sorted roster is rebuilt only when the roster changes.
public sealed class ClientScenePresenter : IEnemyRenderPoseSource
{
    private static readonly TimeSpan DiagnosticsLogInterval = TimeSpan.FromSeconds(1);

    // A newer enemy sample farther than this from the previous one is a teleport (a respawn whose
    // death snapshot was coalesced away, or a server-side reposition): the buffer restarts there
    // instead of sliding across the gap. Wolves move under 3 m/s, so a real step never comes close.
    public const float EnemyTeleportDistance = 4f;

    private string? _localSessionId;
    private string _applyLogKey = null!;
    private readonly Dictionary<int, RemotePlayerEntry> _remotePlayers = [];
    private readonly Dictionary<string, PlayerRosterEntry> _playerRoster = new(StringComparer.Ordinal);

    // One tick-keyed pose buffer per live remote enemy. Entries of pruned enemies return to the
    // pool with their sample storage, so enemies that come and go do not allocate in steady state.
    private readonly Dictionary<int, RemoteEnemyEntry> _remoteEnemies = [];
    private readonly Stack<RemoteEnemyEntry> _remoteEnemyPool = new();

    // Reused outputs. Callers get these lists themselves, not copies; see the Build methods.
    private readonly List<RemotePlayerPresentationState> _remotePlayerStates = [];
    private readonly List<PlayerRosterEntry> _sortedRoster = [];
    private readonly Comparison<PlayerRosterEntry> _rosterComparison;
    private bool _sortedRosterIsStale = true;

    // Reused per-snapshot scratch.
    private readonly HashSet<int> _observedRemoteActorIds = [];
    private readonly HashSet<string> _observedPlayerSessions = new(StringComparer.Ordinal);
    private readonly List<int> _staleRemoteActorIds = [];
    private readonly List<string> _staleRosterSessions = [];
    private readonly HashSet<int> _observedEnemyActorIds = [];
    private readonly List<int> _staleEnemyActorIds = [];

    public ClientScenePresenter(string? localSessionId = null)
    {
        _rosterComparison = CompareRosterEntries;
        SetLocalSessionId(localSessionId);
    }

    public void ApplySnapshot(ServerSnapshotEnvelope snapshot)
    {
        _observedRemoteActorIds.Clear();
        _observedPlayerSessions.Clear();
        _observedEnemyActorIds.Clear();
        IReadOnlyList<ActorSnapshot> actors = snapshot.Actors;
        for (int i = 0; i < actors.Count; i++)
        {
            ActorSnapshot actor = actors[i];
            if (actor.Kind == CreatureKind.Enemy)
            {
                ApplyEnemySample(actor, snapshot.ServerTick);
                continue;
            }

            if (actor.Kind != CreatureKind.Player || string.IsNullOrWhiteSpace(actor.SessionId))
            {
                continue;
            }

            _observedPlayerSessions.Add(actor.SessionId);
            string displayName = string.IsNullOrWhiteSpace(actor.DisplayName)
                ? actor.SessionId
                : actor.DisplayName;
            SetRosterEntry(new PlayerRosterEntry(actor.SessionId, actor.ActorId, displayName));

            if (string.Equals(actor.SessionId, _localSessionId, StringComparison.Ordinal))
            {
                continue;
            }

            _observedRemoteActorIds.Add(actor.ActorId);
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

        // Collected first so neither dictionary is modified while it is enumerated; removal keeps
        // the dictionary's enumeration order, as the old ToArray copies did.
        _staleRemoteActorIds.Clear();
        foreach (int actorId in _remotePlayers.Keys)
        {
            if (!_observedRemoteActorIds.Contains(actorId))
            {
                _staleRemoteActorIds.Add(actorId);
            }
        }

        for (int i = 0; i < _staleRemoteActorIds.Count; i++)
        {
            int actorId = _staleRemoteActorIds[i];
            _remotePlayers.Remove(actorId, out RemotePlayerEntry? entry);
            if (NetworkDiagnosticsLog.IsEnabled)
            {
                LogPrunedRemotePlayer(actorId, entry!.SessionId, snapshot);
            }
        }

        PruneUnobservedEnemies();

        _staleRosterSessions.Clear();
        foreach (string sessionId in _playerRoster.Keys)
        {
            if (string.Equals(sessionId, _localSessionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!_observedPlayerSessions.Contains(sessionId))
            {
                _staleRosterSessions.Add(sessionId);
            }
        }

        for (int i = 0; i < _staleRosterSessions.Count; i++)
        {
            _playerRoster.Remove(_staleRosterSessions[i]);
            _sortedRosterIsStale = true;
        }

        if (NetworkDiagnosticsLog.IsEnabled)
        {
            NetworkDiagnosticsLog.WriteEvery(
                _applyLogKey,
                DiagnosticsLogInterval,
                "client.presenter.apply",
                (Presenter: this, Snapshot: snapshot),
                static state => state.Presenter.DescribeApply(state.Snapshot));
        }
    }

    public void ApplyJoinAccepted(JoinAcceptedMessage joinAccepted)
    {
        if (!string.IsNullOrWhiteSpace(joinAccepted.SessionId))
        {
            SetLocalSessionId(joinAccepted.SessionId);
        }

        // A join starts a new server tick timeline; samples keyed by the old one would never be replaced.
        _remotePlayers.Clear();
        ReleaseAllEnemies();

        foreach (PlayerRosterEntry entry in joinAccepted.EffectiveRoster)
        {
            string displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
                ? entry.SessionId
                : entry.DisplayName;
            SetRosterEntry(new PlayerRosterEntry(entry.SessionId, entry.ActorId, displayName));
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

    // The local player first, then display name (case-insensitive), then session id: the order
    // of the former OrderByDescending(local).ThenBy(name).ThenBy(session). Session ids are unique
    // roster keys, so the order is total and the unstable List.Sort gives the same result.
    //
    // Returns the presenter's own list, not a copy. It stays valid until the next ApplySnapshot or
    // ApplyJoinAccepted changes the roster; the HUD reads it within the frame and keeps no reference.
    public IReadOnlyList<PlayerRosterEntry> BuildPlayerRoster()
    {
        if (_sortedRosterIsStale)
        {
            _sortedRoster.Clear();
            foreach (PlayerRosterEntry entry in _playerRoster.Values)
            {
                _sortedRoster.Add(entry);
            }

            _sortedRoster.Sort(_rosterComparison);
            _sortedRosterIsStale = false;
        }

        return _sortedRoster;
    }

    // Returns the presenter's own list, refilled on every call; it is not a copy. The renderers,
    // the HUD and the diagnostics read it within the frame that built it and keep no reference.
    public IReadOnlyList<RemotePlayerPresentationState> BuildRemotePlayerStates(double renderTick)
    {
        _remotePlayerStates.Clear();
        foreach ((int actorId, RemotePlayerEntry entry) in _remotePlayers)
        {
            if (!entry.Buffer.TrySample(renderTick, out InterpolatedActorPose pose))
            {
                continue;
            }

            _remotePlayerStates.Add(new RemotePlayerPresentationState(
                actorId,
                entry.SessionId,
                entry.DisplayName,
                pose,
                entry.CurrentHealth,
                entry.MaxHealth,
                entry.IsAlive,
                entry.IsMoving));
        }

        return _remotePlayerStates;
    }

    public int RemoteEnemyBufferCount => _remoteEnemies.Count;

    public int GetRemoteEnemySampleCount(int actorId)
    {
        return _remoteEnemies.TryGetValue(actorId, out RemoteEnemyEntry? entry) ? entry.Buffer.Count : 0;
    }

    // The enemy's pose on the server-tick render timeline. False when the enemy has no buffer
    // (never seen, dead or removed); the renderer then falls back to the authoritative pose.
    public bool TryGetEnemyRenderPose(int actorId, double renderTick, out InterpolatedActorPose pose)
    {
        if (_remoteEnemies.TryGetValue(actorId, out RemoteEnemyEntry? entry))
        {
            return entry.Buffer.TrySample(renderTick, out pose);
        }

        pose = default;
        return false;
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

    private void SetLocalSessionId(string? localSessionId)
    {
        _localSessionId = localSessionId;
        _applyLogKey = $"client.presenter.apply.{_localSessionId ?? "none"}";
        _sortedRosterIsStale = true;
    }

    private void SetRosterEntry(PlayerRosterEntry entry)
    {
        if (_playerRoster.TryGetValue(entry.SessionId, out PlayerRosterEntry existing) && existing == entry)
        {
            return;
        }

        _playerRoster[entry.SessionId] = entry;
        _sortedRosterIsStale = true;
    }

    private int CompareRosterEntries(PlayerRosterEntry left, PlayerRosterEntry right)
    {
        bool leftIsLocal = string.Equals(left.SessionId, _localSessionId, StringComparison.Ordinal);
        bool rightIsLocal = string.Equals(right.SessionId, _localSessionId, StringComparison.Ordinal);
        if (leftIsLocal != rightIsLocal)
        {
            return leftIsLocal ? -1 : 1;
        }

        int byName = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
        return byName != 0
            ? byName
            : StringComparer.Ordinal.Compare(left.SessionId, right.SessionId);
    }

    private RemotePlayerEntry GetOrCreateEntry(int actorId, string sessionId)
    {
        // The server reuses a player actor id once its session leaves. An entry under this id from
        // another session is that earlier player: start the new one from an empty buffer instead
        // of sliding the old avatar to the new player.
        if (_remotePlayers.TryGetValue(actorId, out RemotePlayerEntry? entry) &&
            string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
        {
            return entry;
        }

        RemotePlayerEntry created = new(sessionId);
        _remotePlayers[actorId] = created;
        return created;
    }

    private void ApplyEnemySample(ActorSnapshot actor, ulong serverTick)
    {
        // A dead enemy stops interpolating: it is not observed, so its buffer is pruned below and
        // a later respawn under the same id starts from an empty buffer (a teleport, not a slide).
        // This matches RemoteEnemyStateApplier, which removes dead enemies from the registry.
        if (actor.CurrentHealth <= 0 || !actor.IsAlive)
        {
            return;
        }

        _observedEnemyActorIds.Add(actor.ActorId);
        if (!_remoteEnemies.TryGetValue(actor.ActorId, out RemoteEnemyEntry? entry))
        {
            entry = _remoteEnemyPool.Count > 0 ? _remoteEnemyPool.Pop() : new RemoteEnemyEntry();
            entry.LastFacing = -Vector3.UnitZ;
            _remoteEnemies[actor.ActorId] = entry;
        }

        Vector3 facing = actor.Facing;
        facing.Y = 0f;
        facing = facing.LengthSquared > 0.000001f ? facing.Normalized() : entry.LastFacing;
        entry.LastFacing = facing;

        if (entry.Buffer.TryGetNewest(out TickPoseSample newest) &&
            serverTick > newest.ServerTick &&
            (actor.Position - newest.Position).LengthSquared > EnemyTeleportDistance * EnemyTeleportDistance)
        {
            entry.Buffer.Clear();
        }

        entry.Buffer.AddSample(serverTick, actor.Position, facing);
    }

    private void PruneUnobservedEnemies()
    {
        _staleEnemyActorIds.Clear();
        foreach (int actorId in _remoteEnemies.Keys)
        {
            if (!_observedEnemyActorIds.Contains(actorId))
            {
                _staleEnemyActorIds.Add(actorId);
            }
        }

        for (int i = 0; i < _staleEnemyActorIds.Count; i++)
        {
            _remoteEnemies.Remove(_staleEnemyActorIds[i], out RemoteEnemyEntry? entry);
            ReleaseEnemyEntry(entry!);
        }
    }

    private void ReleaseAllEnemies()
    {
        foreach (RemoteEnemyEntry entry in _remoteEnemies.Values)
        {
            ReleaseEnemyEntry(entry);
        }

        _remoteEnemies.Clear();
    }

    private void ReleaseEnemyEntry(RemoteEnemyEntry entry)
    {
        entry.Buffer.Clear();
        _remoteEnemyPool.Push(entry);
    }

    private void LogPrunedRemotePlayer(int actorId, string sessionId, ServerSnapshotEnvelope snapshot)
    {
        if (!NetworkDiagnosticsLog.ShouldLogEvery($"client.presenter.prune.{actorId}", DiagnosticsLogInterval))
        {
            return;
        }

        NetworkDiagnosticsLog.Write(
            "client.presenter.prune",
            $"local={_localSessionId ?? "<none>"} actor={actorId} session={sessionId} snapshotTick={snapshot.ServerTick} " +
                NetworkDiagnosticsLog.DescribeSnapshot(snapshot));
    }

    private string DescribeApply(ServerSnapshotEnvelope snapshot)
    {
        return $"local={_localSessionId ?? "<none>"} remoteEntries={_remotePlayers.Count} roster={DescribeRosterSessions()} " +
            NetworkDiagnosticsLog.DescribeSnapshot(snapshot);
    }

    private string DescribeRosterSessions()
    {
        return $"[{string.Join(", ", _playerRoster.Values.Select(entry => $"{entry.SessionId}#{entry.ActorId}/{entry.DisplayName}"))}]";
    }

    private sealed class RemoteEnemyEntry
    {
        public TickInterpolationBuffer Buffer { get; } = new();

        public Vector3 LastFacing { get; set; } = -Vector3.UnitZ;
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

        public Vector3? LastSnapshotPosition { get; set; }
    }
}

// Interpolated enemy poses for the render path. Implemented by ClientScenePresenter; the enemy
// renderer reads it so gameplay keeps the latest authoritative EnemyActor.Position.
public interface IEnemyRenderPoseSource
{
    bool TryGetEnemyRenderPose(int actorId, double renderTick, out InterpolatedActorPose pose);
}
