using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

// Session seating: attaching, detaching and joining remote sessions, secondary-player
// creation, remote actor id allocation, per-session interaction modes and primary-seat lookups.
public sealed partial class WorldSessionRuntime
{
    // Ordinal by session id, so per-tick iteration over secondary players never depends on join history.
    private static readonly Comparison<RemoteSessionPlayerState> CompareSecondaryBySessionId =
        static (left, right) => string.CompareOrdinal(left.SessionId.Value, right.SessionId.Value);

    // Secondary player actor ids (see AllocateRemoteActorId): the next never-used id, and the ids
    // released by detached sessions, reused lowest first. Player ids are not persisted.
    private int _nextRemoteActorId = 2;
    private readonly SortedSet<int> _releasedRemoteActorIds = [];

    public ConnectedPlayer AttachRemoteSession(PlayerSessionId sessionId, string? displayName = null)
    {
        ConnectedPlayer attached = AttachRemoteSessionCore(sessionId, displayName);
        // An attached player's cached profile is never evicted (MarkProfileDeparted).
        MarkProfileActive(attached.DisplayName);
        return attached;
    }

    private ConnectedPlayer AttachRemoteSessionCore(PlayerSessionId sessionId, string? displayName)
    {
        _primaryPlayerTarget = null;
        if (_sessions.TryGet(sessionId, out ConnectedPlayer? existing) && existing != null && !existing.IsLocal)
        {
            existing.UpdateDisplayName(displayName);
            existing.MarkSpawned();
            _sessionInteractionModes.TryAdd(existing.SessionId, PlayerInteractionMode.Building);
            if (existing.ActorId == Player.Combatant.Actor.Id)
            {
                if (PrimaryRemoteSessionId == null || PrimaryRemoteSessionId == existing.SessionId)
                {
                    SeatPrimary(existing.SessionId);
                    return existing;
                }

                // The primary seat was taken while this player was away. Previously the returning
                // session kept the primary actor id without owning the seat, so it could neither
                // move nor act. Give it its own seat instead.
                return RegisterSecondarySession(existing.SessionId, existing.DisplayName);
            }

            if (!_secondaryRemotePlayers.ContainsKey(existing.SessionId))
            {
                // Its id was released when it detached; another join may have taken it since.
                if (!_releasedRemoteActorIds.Remove(existing.ActorId))
                {
                    return RegisterSecondarySession(existing.SessionId, existing.DisplayName);
                }

                CreateSecondaryPlayer(existing.SessionId, existing.ActorId);
            }

            return existing;
        }

        if (!HasPrimaryRemoteSession())
        {
            ConnectedPlayer primary = _sessions.Register(
                sessionId,
                Player.Combatant.Actor.Id,
                isLocal: false,
                displayName: displayName);
            _sessionInteractionModes[primary.SessionId] = PlayerInteractionMode.Building;
            SeatPrimary(primary.SessionId);
            return primary;
        }

        return RegisterSecondarySession(sessionId, displayName);
    }

    private void SeatPrimary(PlayerSessionId sessionId)
    {
        PrimaryRemoteSessionId = sessionId;
        _primaryPlayerTarget = null;
        Physics.SetPlayerBodyParked(false);
    }

    private ConnectedPlayer RegisterSecondarySession(PlayerSessionId sessionId, string? displayName)
    {
        int actorId = AllocateRemoteActorId();
        CreateSecondaryPlayer(sessionId, actorId);
        ConnectedPlayer remote = _sessions.Register(
            sessionId,
            actorId,
            isLocal: false,
            displayName: displayName);
        _sessionInteractionModes[remote.SessionId] = PlayerInteractionMode.Building;
        return remote;
    }

    private void CreateSecondaryPlayer(PlayerSessionId sessionId, int actorId)
    {
        Vector3 spawnPosition = ResolveSecondaryRemoteSpawnPosition(_secondaryRemotePlayers.Count);
        _secondaryRemotePlayers[sessionId] = new RemoteSessionPlayerState(
            actorId,
            sessionId,
            spawnPosition,
            Map,
            Physics,
            combatFormulaService: _combatFormulaService);
        RebuildOrderedSecondaryRemotePlayers();
    }

    private void RebuildOrderedSecondaryRemotePlayers()
    {
        RemoteSessionPlayerState[] ordered = new RemoteSessionPlayerState[_secondaryRemotePlayers.Count];
        _secondaryRemotePlayers.Values.CopyTo(ordered, 0);
        Array.Sort(ordered, CompareSecondaryBySessionId);
        _orderedSecondaryRemotePlayers = ordered;
    }

    public void DetachRemoteSession(PlayerSessionId sessionId)
    {
        if (!_sessions.TryGet(sessionId, out ConnectedPlayer? player) ||
            player == null ||
            player.IsLocal)
        {
            return;
        }

        PersistPlayerProfile(sessionId, TryResolveAuthoritativePlayerTarget(
            sessionId,
            out _,
            out IAuthoritativePlayerTarget? target)
                ? target!.Combatant
                : Player.Combatant);
        // Position, facing and HP, queued behind the profile; checkpoints skip detached players.
        _persistence.PersistPlayerWorldState(sessionId);
        _persistence.ForgetUnrestoredSession(sessionId);
        player.MarkDespawned();
        player.ResetAcceptedSequences();
        // After MarkDespawned, so the leaving session no longer counts as holding its name. The
        // profile was just saved, so the cached copy may now be evicted.
        MarkProfileDeparted(player.DisplayName);
        _primaryPlayerTarget = null;

        if (PrimaryRemoteSessionId == sessionId)
        {
            PrimaryRemoteSessionId = null;
            _lastAppliedCommandFrame = CreateIdleCommandFrame(LocalPlayerSessionId, sequence: 0);
            _primaryTicksSinceCommand = 0;
            // Without an occupant the primary body would stay as an invisible collider.
            Physics.SetPlayerBodyParked(true);
        }
        else if (_secondaryRemotePlayers.Remove(sessionId, out RemoteSessionPlayerState? secondary))
        {
            RebuildOrderedSecondaryRemotePlayers();
            _experienceToNextLevelByActor.Remove(secondary.ActorId);
            ReleaseRemoteActorId(secondary.ActorId);
            secondary.Dispose();
        }
    }

    // The name a join restores its persisted state under: the trimmed display name, or the session
    // id when the name is blank (ConnectedPlayer's rule, which the joined player ends up with).
    public static string ResolveJoinPlayerName(string requestedSessionId, string? displayName)
    {
        return string.IsNullOrWhiteSpace(displayName) ? requestedSessionId : displayName.Trim();
    }

    // The registry dropped this session (LRU eviction of a departed session, or an entry replaced
    // under the same id, which the caller re-seeds right after). Its mode goes with it, so
    // _sessionInteractionModes stays bounded by the registry. A remembered session keeps its mode
    // across a reattach (AttachRemoteSession's TryAdd).
    private void OnSessionForgotten(ConnectedPlayer player)
    {
        _sessionInteractionModes.Remove(player.SessionId);
    }

    // Sessions with a stored interaction mode (tests): at most the registry's Count.
    internal int SessionInteractionModeCount => _sessionInteractionModes.Count;

    private PlayerInteractionMode GetSessionInteractionMode(PlayerSessionId sessionId)
    {
        if (_secondaryRemotePlayers.TryGetValue(sessionId, out RemoteSessionPlayerState? remote))
        {
            return remote.InteractionMode;
        }

        return _sessionInteractionModes.TryGetValue(sessionId, out PlayerInteractionMode interactionMode)
            ? interactionMode
            : PlayerInteractionMode.Building;
    }

    private void SetSessionInteractionMode(PlayerSessionId sessionId, PlayerInteractionMode interactionMode)
    {
        _sessionInteractionModes[sessionId] = interactionMode;
        if (_secondaryRemotePlayers.TryGetValue(sessionId, out RemoteSessionPlayerState? remote))
        {
            remote.SetInteractionMode(interactionMode);
        }

        if (_playerProfiles == null
            || !_sessions.TryGet(sessionId, out ConnectedPlayer? player)
            || player == null
            || player.IsLocal)
        {
            return;
        }

        PersistedPlayerProfile profile = GetCachedProfile(player.DisplayName);
        SaveProfile(player.DisplayName, profile with
        {
            DisplayName = player.DisplayName,
            InteractionMode = interactionMode
        });
    }

    private PlayerSessionId ResolvePrimaryControlledSessionId()
    {
        if (PrimaryRemoteSessionId.HasValue &&
            _sessions.TryGet(PrimaryRemoteSessionId.Value, out ConnectedPlayer? primary) &&
            primary != null &&
            !primary.IsLocal &&
            primary.IsSpawned)
        {
            return primary.SessionId;
        }

        return LocalPlayerSessionId;
    }

    private bool HasPrimaryRemoteSession()
    {
        return PrimaryRemoteSessionId.HasValue &&
            _sessions.TryGet(PrimaryRemoteSessionId.Value, out ConnectedPlayer? primary) &&
            primary != null &&
            !primary.IsLocal &&
            primary.IsSpawned &&
            primary.ActorId == Player.Combatant.Actor.Id;
    }

    private bool IsPrimaryControlledSession(PlayerSessionId sessionId)
    {
        return sessionId == ResolvePrimaryControlledSessionId();
    }

    // A secondary player's actor id, always in the player range (ReplicatedActorIdRanges): the
    // lowest id a detached session released, else the next never-used id. A client picks a fresh
    // session id on every connect, so without reuse a long-running server would hand out an id
    // per join and reach the cube range after about a thousand joins. Deterministic: the same join
    // and leave sequence allocates the same ids. The primary seat keeps Player's own id.
    private int AllocateRemoteActorId()
    {
        if (_releasedRemoteActorIds.Count > 0)
        {
            int reused = _releasedRemoteActorIds.Min;
            _releasedRemoteActorIds.Remove(reused);
            return reused;
        }

        int actorId = Math.Max(_nextRemoteActorId, Player.Combatant.Actor.Id + 1);
        _nextRemoteActorId = ReplicatedActorIdRanges.NextPlayerId(actorId);
        return actorId;
    }

    // A detached secondary session's id becomes free for the next join. Its ConnectedPlayer keeps
    // the id, so a later reattach of that session reclaims it only if no one took it meanwhile
    // (see AttachRemoteSession).
    private void ReleaseRemoteActorId(int actorId)
    {
        if (actorId != Player.Combatant.Actor.Id)
        {
            _releasedRemoteActorIds.Add(actorId);
        }
    }

    private Vector3 ResolveSecondaryRemoteSpawnPosition(int remoteIndex)
    {
        float laneOffset = 2f + remoteIndex;
        Vector3 basePosition = Player.GetWorldPosition();
        return new Vector3(
            Math.Clamp(basePosition.X + laneOffset, 0.5f, Map.Width - 0.5f),
            1.5f,
            Math.Clamp(basePosition.Z + 2f, 0.5f, Map.Depth - 0.5f));
    }

    private string TryResolveDisplayName(PlayerSessionId sessionId)
    {
        return _sessions.TryGet(sessionId, out ConnectedPlayer? player) && player != null
            ? player.DisplayName
            : sessionId.Value;
    }
}
