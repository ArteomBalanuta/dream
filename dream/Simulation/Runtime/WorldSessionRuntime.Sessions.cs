using OpenTK.Mathematics;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Enemies;
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

    // character: the persistence key and display name a checked join resolved (JoinSession with an
    // access decision); null leaves the key following the typed display name, as on hosts without accounts.
    public ConnectedPlayer AttachRemoteSession(
        PlayerSessionId sessionId,
        string? displayName = null,
        Dream.Simulation.Characters.CharacterKey? character = null)
    {
        bool alreadyAttached = _sessions.TryGet(sessionId, out ConnectedPlayer? before) && before != null && before.IsSpawned;
        ConnectedPlayer attached = AttachRemoteSessionCore(sessionId, displayName);
        if (character is { } key)
        {
            attached.BindCharacter(key, displayName ?? key.Value);
        }

        // Every join, first or returning, is protected for the same window as a respawn and it ends on the
        // player's first attack. Without it a new player who stands still where they joined was bitten within
        // 9 ticks and killed in about 8.6 s, then looped through respawns (a wolf spawned near the world
        // spawn). The window is counted in server ticks and belongs to the character, not the seat: a rejoin
        // resumes what is left of the one it already had and earns a new one only after the reattach
        // cooldown. A repeated join of a seat that is already attached does not start a new window.
        // The join's opt-in hold is honoured only on a fresh attach that is not a quick re-seat of the same
        // session (a takeover detaches, then attaches fresh): see ConnectedPlayer.LastFreshAttachTick.
        attached.FreshlyAttached = !alreadyAttached
            && (attached.LastFreshAttachTick == 0
                || CurrentServerTick >= attached.LastFreshAttachTick + PlayerRespawnRules.JoinHoldReattachCooldownTicks);
        if (!alreadyAttached)
        {
            attached.LastFreshAttachTick = Math.Max(1UL, CurrentServerTick);
            // BH-PVP-1: the window is the character's, not the seat's. A rejoin resumes what is left of it
            // (nothing, once an attack spent it) and starts a new one only after the reattach cooldown, so
            // dropping the connection mid-fight can no longer buy fresh immunity. See ApplyJoinSpawnProtection.
            ApplyJoinSpawnProtection(attached);
            // BH-SKILL-2: a join no longer starts clean. Cooldowns are a time cost and belong to the
            // character, like the spawn-protection window above, so a reconnect resumes the ones still
            // running instead of wiping them (mana, health and the potions in the bag already persist).
            // The owner gets its vitals again either way.
            RestoreCharacterCooldowns(attached);
            attached.VitalsDirty = true;
            attached.LastSentMana = -1;
            attached.LastSentMaxMana = -1;
        }

        // An attached player's cached profile is never evicted (MarkProfileDeparted).
        MarkProfileActive(attached.CharacterKey.Value);
        return attached;
    }

    // BH-SKILL-2. Cooldowns used to live only on the seat, and AttachRemoteSession cleared them, so
    // dropping the connection and rejoining handed back every running cooldown while mana, health and
    // the bag survived -- a free reset of the only thing a cast costs besides mana. They are tracked
    // against the CHARACTER as well now, exactly as the spawn-protection window is (see
    // ApplyJoinSpawnProtection): a cooldown is recorded here whenever a seat starts one, and a fresh
    // attach restores what is still running at the current server tick rather than clearing.
    //
    // Two live seats can share a CharacterKey (wave 16), which is why the record is written on every
    // Start rather than on detach: a second seat joining the same character inherits the cooldowns the
    // first one is already serving, so a seat swap cannot be used to defeat this either. Everything is
    // in server ticks, so lockstep peers agree, and the restore keeps the recorded order.
    private readonly Dictionary<string, List<AbilityCooldown>> _characterCooldowns = new(StringComparer.Ordinal);

    // An entry whose cooldowns have all expired grants exactly what no entry grants, so the table is
    // pruned of those once it grows past this. The removed set is a function of the tick and the
    // contents, never of iteration order.
    private const int CharacterCooldownCap = 512;

    // Starts a cooldown on the seat and records it against the character.
    private void StartCooldown(ConnectedPlayer player, string key, ulong readyTick, ulong nowTick)
    {
        player.Cooldowns.Start(key, readyTick, nowTick);

        string character = player.CharacterKey.Value;
        if (!_characterCooldowns.TryGetValue(character, out List<AbilityCooldown>? entries))
        {
            PruneCharacterCooldowns(nowTick);
            entries = [];
            _characterCooldowns[character] = entries;
        }

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].ReadyTick <= nowTick
                || string.Equals(entries[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                entries.RemoveAt(i);
            }
        }

        entries.Add(new AbilityCooldown(key, readyTick));
    }

    // A fresh attach: the seat's list is replaced by the character's cooldowns that are still running.
    private void RestoreCharacterCooldowns(ConnectedPlayer attached)
    {
        attached.Cooldowns.Clear();
        if (!_characterCooldowns.TryGetValue(attached.CharacterKey.Value, out List<AbilityCooldown>? entries))
        {
            return;
        }

        ulong tick = CurrentServerTick;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].ReadyTick <= tick)
            {
                entries.RemoveAt(i);
            }
        }

        for (int i = 0; i < entries.Count; i++)
        {
            attached.Cooldowns.Start(entries[i].Key, entries[i].ReadyTick);
        }
    }

    private void PruneCharacterCooldowns(ulong tick)
    {
        if (_characterCooldowns.Count < CharacterCooldownCap)
        {
            return;
        }

        List<string>? stale = null;
        foreach (KeyValuePair<string, List<AbilityCooldown>> entry in _characterCooldowns)
        {
            bool running = false;
            for (int i = 0; i < entry.Value.Count; i++)
            {
                if (entry.Value[i].ReadyTick > tick)
                {
                    running = true;
                    break;
                }
            }

            if (!running)
            {
                (stale ??= []).Add(entry.Key);
            }
        }

        if (stale == null)
        {
            return;
        }

        for (int i = 0; i < stale.Count; i++)
        {
            _characterCooldowns.Remove(stale[i]);
        }
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

        // Position, facing and HP first, then the profile: the order is load-bearing. A checkpoint
        // captured before this leave can be written (on its own worker) between the two queued
        // writes, and its vitals guard skips the player only once this world-state row is stored
        // (SqlitePlayerProfileRepository.CheckpointUpdateVitalsSql). World state first, a late
        // checkpoint runs either before both (the profile save then overwrites its vitals) or after
        // the world state (its vitals are skipped). Profile first, a checkpoint between the two
        // would overwrite the leave's HP with its stale vitals. Checkpoints skip detached players.
        _persistence.PersistPlayerWorldState(sessionId);
        // An unresolved target is skipped: the primary Combatant must never stand in for it.
        if (TryResolveAuthoritativePlayerTarget(sessionId, out _, out IAuthoritativePlayerTarget? target)
            && target != null)
        {
            PersistPlayerProfile(sessionId, target.Combatant);
        }

        // That save carried the live mode (RuntimePersistenceBridge reads GetSessionInteractionMode),
        // so a paced write waiting for this session has nothing left to write.
        _pendingModeSaves.Remove(sessionId);
        _lastModeSaveTick.Remove(sessionId);
        _persistence.ForgetUnrestoredSession(sessionId);
        // The build material is per connection: a rejoin starts from Auto, as its HUD does.
        _buildMaterialSelections.Remove(sessionId);
        OnSessionDetachedMultiplayer(sessionId, player.CharacterKey.Value);
        player.MarkDespawned();
        player.ResetAcceptedSequences();
        // Wave I: a rejoin that finds the character dead waits a fresh 5 s, and an undrained push for
        // a seat that left has nowhere to go.
        player.RespawnAtTick = 0;
        player.ProtectedThroughTick = 0;
        player.ProtectionHeldSinceTick = 0;
        _pendingLoot.Remove(sessionId);
        // A swing in flight dies with the occupant: the primary seat's combatant is reused, so a rejoin would
        // otherwise pass the same-combatant check of the swing's re-validation and finish it.
        CancelMeleeSwings(sessionId);
        // A session id can be reused by a later join: it must not inherit this one's kill credit.
        IReadOnlyList<EnemyActor> creditedEnemies = _enemies.ActiveActors;
        for (int i = 0; i < creditedEnemies.Count; i++)
        {
            creditedEnemies[i].ClearContributor(sessionId);
        }

        // After MarkDespawned, so the leaving session no longer counts as holding its name. The
        // profile was just saved, so the cached copy may now be evicted.
        MarkProfileDeparted(player.CharacterKey.Value);
        _primaryPlayerTarget = null;

        if (PrimaryRemoteSessionId == sessionId)
        {
            PrimaryRemoteSessionId = null;
            _lastAppliedCommandFrame = CreateIdleCommandFrame(LocalPlayerSessionId, sequence: 0);
            _primaryTicksSinceCommand = 0;
            // Without an occupant the primary body would stay as an invisible collider.
            Physics.SetPlayerBodyParked(true);
            Player.ResetSeatState();
            // The seat's combatant is reused: nothing of this occupant's mana carries to the next.
            Player.Combatant.SetMana(Player.Combatant.MaxMana);
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
        _buildMaterialSelections.Remove(player.SessionId);
        _pendingLoot.Remove(player.SessionId);
        _lastModeSaveTick.Remove(player.SessionId);
        _pendingModeSaves.Remove(player.SessionId);
    }

    // Sessions with a stored interaction mode (tests): at most the registry's Count.
    internal int SessionInteractionModeCount => _sessionInteractionModes.Count;

    // At most one mode profile save per session per second. The change test below stops a starved
    // (honest) seat's repeated request, but a client that alternates RequestedMode every tick changes
    // the value every tick by construction, so the test passes and one SQLite profile write is queued
    // per tick per connection - a remote write amplifier against the shared persistence queue, which
    // every legitimate write (loot, inventory, sanctions, checkpoints) then queues behind. The mode
    // itself stays immediate: only the write is paced, and the last mode of a paced burst is written
    // by StepPendingModeSaves once the interval has passed, so a flip a player can actually make
    // (a keypress) is persisted within a second of the press and the detach save writes the live mode
    // in any case. Server ticks, never wall time, so the authoritative path stays deterministic.
    internal const int ModeSaveIntervalTicks = SimulationClock.AuthoritativeTicksPerSecond;

    private readonly Dictionary<PlayerSessionId, ulong> _lastModeSaveTick = [];
    private readonly Dictionary<PlayerSessionId, PlayerInteractionMode> _pendingModeSaves = [];
    private readonly List<PlayerSessionId> _modeSaveFlushScratch = [];

    // Mode profile writes the pacing collapsed since this runtime started: an operator sees a
    // per-tick flipper here and in the server.session.mode-save-paced line.
    internal int ModeSavesPaced { get; private set; }

    internal int PendingModeSaveCount => _pendingModeSaves.Count;

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

    // Saves the profile only when the mode changes (or saveUnchanged, the join's restore): a starved
    // seat's frames keep the mode it already has (RequestedMode), which used to queue a profile save
    // every tick.
    private void SetSessionInteractionMode(
        PlayerSessionId sessionId,
        PlayerInteractionMode interactionMode,
        bool saveUnchanged = false)
    {
        bool save = saveUnchanged || GetSessionInteractionMode(sessionId) != interactionMode;
        _sessionInteractionModes[sessionId] = interactionMode;
        if (_secondaryRemotePlayers.TryGetValue(sessionId, out RemoteSessionPlayerState? remote))
        {
            remote.SetInteractionMode(interactionMode);
        }

        if (!save
            || _playerProfiles == null
            || !_sessions.TryGet(sessionId, out ConnectedPlayer? player)
            || player == null
            || player.IsLocal)
        {
            return;
        }

        // The join's restore (saveUnchanged) is server driven, once per join, and is never paced; it
        // also does not stamp the interval, so a player's first mode change after joining is immediate.
        if (!saveUnchanged
            && _lastModeSaveTick.TryGetValue(sessionId, out ulong lastSaveTick)
            && CurrentServerTick - lastSaveTick < (ulong)ModeSaveIntervalTicks)
        {
            _pendingModeSaves[sessionId] = interactionMode;
            ModeSavesPaced++;
            NetworkDiagnosticsLog.Write(
                "server.session.mode-save-paced",
                $"session={sessionId.Value} tick={CurrentServerTick}");
            return;
        }

        if (!saveUnchanged)
        {
            _lastModeSaveTick[sessionId] = CurrentServerTick;
        }

        _pendingModeSaves.Remove(sessionId);
        SaveSessionInteractionModeProfile(player, interactionMode);
    }

    private void SaveSessionInteractionModeProfile(ConnectedPlayer player, PlayerInteractionMode interactionMode)
    {
        PersistedPlayerProfile profile = GetCachedProfile(player.CharacterKey.Value);
        SaveProfile(player.CharacterKey.Value, profile with
        {
            DisplayName = player.DisplayName,
            InteractionMode = interactionMode
        });
    }

    // Writes the last mode of a paced burst once its session's interval has passed. Called once a tick.
    private void StepPendingModeSaves(ulong tick)
    {
        if (_pendingModeSaves.Count == 0)
        {
            return;
        }

        _modeSaveFlushScratch.Clear();
        foreach (KeyValuePair<PlayerSessionId, PlayerInteractionMode> entry in _pendingModeSaves)
        {
            if (!_lastModeSaveTick.TryGetValue(entry.Key, out ulong lastSaveTick)
                || tick - lastSaveTick >= (ulong)ModeSaveIntervalTicks)
            {
                _modeSaveFlushScratch.Add(entry.Key);
            }
        }

        for (int i = 0; i < _modeSaveFlushScratch.Count; i++)
        {
            PlayerSessionId sessionId = _modeSaveFlushScratch[i];
            if (!_pendingModeSaves.Remove(sessionId, out PlayerInteractionMode interactionMode))
            {
                continue;
            }

            if (_playerProfiles == null
                || !_sessions.TryGet(sessionId, out ConnectedPlayer? player)
                || player == null
                || player.IsLocal)
            {
                continue;
            }

            _lastModeSaveTick[sessionId] = tick;
            SaveSessionInteractionModeProfile(player, interactionMode);
        }

        _modeSaveFlushScratch.Clear();
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
        Vector3 basePosition = _worldSpawn;
        return new Vector3(
            Math.Clamp(basePosition.X + laneOffset, 0.5f, Map.Width - 0.5f),
            1.5f,
            Math.Clamp(basePosition.Z + 2f, 0.5f, Map.Depth - 0.5f));
    }

    // Display text only (rosters, actor names). Persistence uses TryResolveCharacterKey.
    private string TryResolveDisplayName(PlayerSessionId sessionId)
    {
        return _sessions.TryGet(sessionId, out ConnectedPlayer? player) && player != null
            ? player.DisplayName
            : sessionId.Value;
    }

    // BH-CLOCK-4. The character behind a live session, for host state that has to survive a
    // reconnect (the chat token bucket). False for a session the runtime does not know.
    public bool TryGetCharacterKey(PlayerSessionId sessionId, out string characterKey)
    {
        if (_sessions.TryGet(sessionId, out ConnectedPlayer? player) && player != null)
        {
            characterKey = player.CharacterKey.Value;
            return true;
        }

        characterKey = string.Empty;
        return false;
    }

    // The persistence key of the session's character (the session id for an unknown session).
    private string TryResolveCharacterKey(PlayerSessionId sessionId)
    {
        return _sessions.TryGet(sessionId, out ConnectedPlayer? player) && player != null
            ? player.CharacterKey.Value
            : sessionId.Value;
    }
}
