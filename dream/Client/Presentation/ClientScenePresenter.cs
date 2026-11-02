using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Dream.Rendering.Actors;
using Dream.Simulation.Runtime;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using OpenTK.Mathematics;

namespace Dream.Client.Presentation;

// Runs on every render frame (BuildRemotePlayerStates, BuildPlayerRoster) and on every snapshot
// (ApplySnapshot), so steady state allocates nothing: scratch collections are reused and the
// sorted roster is rebuilt only when the roster changes.
public sealed class ClientScenePresenter : IEnemyRenderPoseSource, IPlayerActorSessionHistory
{
    private static readonly TimeSpan DiagnosticsLogInterval = TimeSpan.FromSeconds(1);

    // A newer enemy sample farther than this from the previous one is a teleport (a respawn whose
    // death snapshot was coalesced away, or a server-side reposition): the buffer restarts there
    // instead of sliding across the gap. Wolves move under 3 m/s, so a real step never comes close.
    public const float EnemyTeleportDistance = 4f;

    // Wire levels are hostile; a level shown beside health is clamped to 1..9999.
    public const int MaxDisplayLevel = 9999;

    // A remote swing is inferred, not sent: a player in combat mode with a struck enemy this close,
    // in front of it, is swinging. Vertical movement between snapshots above this speed (m/s) is a
    // jump or a fall.
    public const float InferredAttackReach = 3.5f;
    public const float InferredAttackMinForwardDot = 0.25f;
    public const float AirborneVerticalSpeed = 0.8f;

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

    // The session of every player actor (local included) in the last snapshot applied, and the
    // one being filled from the snapshot in progress; swapped at the end of ApplySnapshot. Join
    // state never seeds it: see ApplyJoinAccepted.
    private Dictionary<int, string> _lastSnapshotPlayerSessions = [];
    private Dictionary<int, string> _currentSnapshotPlayerSessions = [];

    // Reused per-snapshot scratch.
    private readonly HashSet<int> _observedRemoteActorIds = [];
    private readonly HashSet<string> _observedPlayerSessions = new(StringComparer.Ordinal);
    private readonly List<int> _staleRemoteActorIds = [];
    private readonly List<string> _staleRosterSessions = [];
    private readonly HashSet<int> _observedEnemyActorIds = [];
    private readonly List<int> _staleEnemyActorIds = [];
    private readonly List<Vector3> _struckEnemyPositions = [];

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
        _currentSnapshotPlayerSessions.Clear();
        IReadOnlyList<ActorSnapshot> actors = snapshot.Actors;
        CollectStruckEnemyPositions(snapshot, actors);
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
            _currentSnapshotPlayerSessions[actor.ActorId] = actor.SessionId;
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
            if (entry.Seen && actor.IsAlive && actor.CurrentHealth < entry.CurrentHealth)
            {
                entry.HitSerial++;
            }

            entry.CurrentHealth = actor.CurrentHealth;
            entry.MaxHealth = actor.MaxHealth;
            if (actor.IsAlive && !entry.IsAlive)
            {
                // Dead to alive is a respawn: the avatar reappears at the spawn instead of sliding
                // there from where it died.
                entry.Buffer.Clear();
            }

            if (!actor.IsAlive && (entry.IsAlive || !entry.Seen))
            {
                // Seen dying: the death animation plays. First seen already dead: the death is old
                // news, so the avatar stays hidden.
                entry.DiedAtTimestamp = entry.Seen ? Stopwatch.GetTimestamp() : 0L;
            }
            else if (actor.IsAlive)
            {
                entry.DiedAtTimestamp = 0L;
            }

            entry.IsAlive = actor.IsAlive;
            ApplyEquipmentAndMotion(entry, actor, snapshot.ServerTick);
            entry.Seen = true;
            entry.Level = actor.Progression is { } progression
                ? Math.Clamp(progression.Level, 1, MaxDisplayLevel)
                : entry.Level;
            entry.HeldLight = actor.HeldLight;
            entry.IsMoving = entry.LastSnapshotPosition.HasValue &&
                (actor.Position - entry.LastSnapshotPosition.Value).LengthSquared > 0.0001f;
            entry.LastSnapshotPosition = actor.Position;
            entry.LastSnapshotTick = snapshot.ServerTick;
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
        (_lastSnapshotPlayerSessions, _currentSnapshotPlayerSessions) = (_currentSnapshotPlayerSessions, _lastSnapshotPlayerSessions);
        _currentSnapshotPlayerSessions.Clear();

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

        // The join's actors are the state at the join, but damage events the server queued before
        // it (from a player who left, perhaps under an actor id now reused) still arrive with the
        // next snapshot. Only a snapshot is a baseline for attributing damage, so the history
        // starts empty.
        _lastSnapshotPlayerSessions.Clear();
    }

    // The player session that held the actor id in the last snapshot applied. ClientSnapshotDamageApplier
    // reads it before this presenter applies the next snapshot.
    public bool TryGetLastSnapshotPlayerSession(int actorId, [NotNullWhen(true)] out string? sessionId)
    {
        return _lastSnapshotPlayerSessions.TryGetValue(actorId, out sessionId);
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
                entry.IsMoving,
                entry.HeldLight,
                entry.Level,
                entry.MainHandVisual,
                entry.OffHandVisual,
                entry.AttackSerial,
                entry.HitSerial,
                entry.Airborne,
                entry.IsMoving ? entry.HorizontalSpeed : 0f,
                entry.DiedAtTimestamp == 0L
                    ? float.PositiveInfinity
                    : (float)Stopwatch.GetElapsedTime(entry.DiedAtTimestamp).TotalSeconds));
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

    // The held items from the player VisualKey, how fast and how vertically it moved since the
    // previous snapshot, and whether it swung at a struck enemy. The held visuals stay as they were
    // when a delta leaves the key out.
    private void ApplyEquipmentAndMotion(RemotePlayerEntry entry, ActorSnapshot actor, ulong serverTick)
    {
        if (actor.VisualKey is not null)
        {
            PlayerEquipmentVisuals visuals = PlayerEquipmentVisuals.Parse(actor.VisualKey);
            entry.MainHandVisual = visuals.MainHand;
            entry.OffHandVisual = visuals.OffHand;
        }

        if (entry.LastSnapshotPosition.HasValue && serverTick > entry.LastSnapshotTick)
        {
            float seconds = (serverTick - entry.LastSnapshotTick) / (float)SimulationClock.AuthoritativeTicksPerSecond;
            Vector3 delta = actor.Position - entry.LastSnapshotPosition.Value;
            float horizontal = new Vector2(delta.X, delta.Z).Length / seconds;
            // Smoothed, so a late snapshot (a long gap, then a long step) does not jerk the playback speed.
            entry.HorizontalSpeed += (horizontal - entry.HorizontalSpeed) * 0.5f;
            entry.Airborne = MathF.Abs(delta.Y / seconds) > AirborneVerticalSpeed;
        }

        if (actor.IsAlive &&
            actor.InteractionMode == PlayerInteractionMode.Combat &&
            _struckEnemyPositions.Count > 0 &&
            IsSwingingAtStruckEnemy(actor.Position, actor.Facing))
        {
            entry.AttackSerial++;
        }
    }

    private bool IsSwingingAtStruckEnemy(Vector3 position, Vector3 facing)
    {
        Vector2 forward = new(facing.X, facing.Z);
        if (forward.LengthSquared < 0.0001f)
        {
            return false;
        }

        forward.Normalize();
        for (int i = 0; i < _struckEnemyPositions.Count; i++)
        {
            Vector2 toEnemy = new(_struckEnemyPositions[i].X - position.X, _struckEnemyPositions[i].Z - position.Z);
            float distance = toEnemy.Length;
            if (distance > InferredAttackReach)
            {
                continue;
            }

            if (distance < 0.5f || Vector2.Dot(forward, toEnemy / distance) >= InferredAttackMinForwardDot)
            {
                return true;
            }
        }

        return false;
    }

    // The positions of the enemies this snapshot damage events struck.
    private void CollectStruckEnemyPositions(ServerSnapshotEnvelope snapshot, IReadOnlyList<ActorSnapshot> actors)
    {
        _struckEnemyPositions.Clear();
        IReadOnlyList<DamageEventDelta> damage = snapshot.DamageEvents;
        for (int i = 0; i < damage.Count; i++)
        {
            DamageEventDelta evt = damage[i];
            if (evt.Kind != CreatureKind.Enemy || (evt.Amount <= 0 && !evt.ActorDied))
            {
                continue;
            }

            for (int a = 0; a < actors.Count; a++)
            {
                if (actors[a].ActorId == evt.ActorId && actors[a].Kind == CreatureKind.Enemy)
                {
                    _struckEnemyPositions.Add(actors[a].Position);
                    break;
                }
            }
        }
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

        // From the actor's progression group, clamped for display; kept when a delta omits it.
        public int Level { get; set; } = 1;

        public bool IsMoving { get; set; }

        // T2f: the latest snapshot's held light, not interpolated (it switches with the snapshot).
        public HeldLightKind HeldLight { get; set; }

        public Vector3? LastSnapshotPosition { get; set; }

        public ulong LastSnapshotTick { get; set; }

        // False until the first snapshot of this entry has been applied.
        public bool Seen { get; set; }

        public string? MainHandVisual { get; set; }

        public string? OffHandVisual { get; set; }

        // Event counters for the animation: a new value is a new swing or a new hit taken.
        public int AttackSerial { get; set; }

        public int HitSerial { get; set; }

        public bool Airborne { get; set; }

        public float HorizontalSpeed { get; set; }

        // Stopwatch timestamp of the death seen; 0 when alive or already dead when first seen.
        public long DiedAtTimestamp { get; set; }
    }
}

// Interpolated enemy poses for the render path. Implemented by ClientScenePresenter; the enemy
// renderer reads it so gameplay keeps the latest authoritative EnemyActor.Position.
public interface IEnemyRenderPoseSource
{
    bool TryGetEnemyRenderPose(int actorId, double renderTick, out InterpolatedActorPose pose);
}
