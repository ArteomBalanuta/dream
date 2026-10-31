using System.Collections.Concurrent;
using OpenTK.Mathematics;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Persistence;

// The authoritative runtime's persistence (ARCH-3 step B), extracted from WorldSessionRuntime: the
// world restore (cubes, fracture groups, enemies and the id counters seeded from them), the join
// prefetch and the persistence half of a player restore, the profile cache with its departed-name
// LRU, the cube, fracture, enemy and profile write-through, and (RuntimePersistenceBridge.Checkpoints)
// full, incremental and final checkpoints. It talks to the database only through the persistence
// ports and to the runtime only through IRuntimePersistenceHost and the registries it is given.
// Everything but BeginPersistedPlayerPrefetch, GetPersistedPlayerLoadState and
// DiscardPersistedPlayerPrefetch runs on the tick thread.
internal sealed partial class RuntimePersistenceBridge
{
    private readonly IRuntimePersistenceHost _host;
    private readonly IPlayerProfileRepository? _playerProfiles;
    private readonly IWorldPersistenceRepository? _worldPersistence;
    private readonly IPersistenceCheckpointWriter? _checkpointWriter;
    // Immediate writes go through an ordered queue (background on the dedicated server) and the
    // identity/profile lookups the tick needs are served from memory, so gameplay actions no
    // longer read whole tables or commit on the simulation thread.
    private readonly PersistenceWriteQueue? _writes;
    private readonly WorldObjectRegistry _worldObjects;
    private readonly EnemyRegistry _enemies;
    private readonly SceneEventHub _events;
    // Wave C: the runtime's object id counter and its ObjectId <-> actor id map. Persisted cubes
    // and enemies have an ObjectId (never reused) bound to their current actor id; the actor id stays
    // the identity on the wire, in checksums and in RNG seeds. Ids are allocated (Take) only when the
    // runtime has world persistence, on the tick thread, so lockstep servers loading one database
    // allocate the same ids.
    private readonly ObjectIdAllocator _objectIds;
    private readonly ObjectIdentityMap _objectIdentities;
    // The cube DeletePersistedPlacedCube retired last, kept for one call: the combat resolver
    // deletes a fractured cube and then persists its rubble in the same tick, and the rubble's
    // origin is that cube's ObjectId, whose binding the delete already removed.
    private RetiredCube _lastRetiredCube;
    // Cubes deleted since the previous capture; the incremental checkpoint's cube tombstones.
    private readonly List<ObjectId> _removedCubeObjectIds = [];

    private readonly Dictionary<GeometryCellKey, PersistedFractureGroup> _persistedFractureGroups = [];
    // Base-terrain cells whose removal is persisted (restored overrides and this run's), so a cell is
    // written once. Overrides are never checkpointed.
    private readonly HashSet<GeometryCellKey> _persistedTerrainOverrides = [];
    private readonly HashSet<ObjectId> _persistedTerrainOverrideIds = [];
    // Fracture groups (origin ids) replaced since the previous capture; the incremental
    // checkpoint's rubble tombstones for a cell fractured twice.
    private readonly List<ObjectId> _removedFractureOriginIds = [];
    // Profiles by player name: every active player's, plus those of the players who left most
    // recently (see MarkProfileDeparted), so it is bounded however many names clients make up.
    private readonly Dictionary<string, PersistedPlayerProfile> _profileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownProfileNames = new(StringComparer.OrdinalIgnoreCase);
    // Sessions whose join failed before their saved state was restored (TryLoadPlayerForRestore);
    // their in-memory stats are defaults, so PersistPlayerProfile must not save them over the row.
    // Keyed by session, not display name, so another session sharing the name still saves; an
    // entry goes when the session restores or is detached.
    private readonly HashSet<PlayerSessionId> _unrestoredPlayerSessions = [];
    // The world state each departed player left with (PersistPlayerWorldState), by player name. Its
    // write is queued, but a join prefetch started before the leave was applied (a reconnect whose
    // old connection is released after the new one asked to join) read the row before that write,
    // so TryLoadPlayerForRestore prefers this newer copy, as it prefers the cached profile. Bounded
    // like the profile cache: an entry goes when its name rejoins, is captured attached by a
    // checkpoint, or has its profile evicted.
    private readonly Dictionary<string, PersistedPlayerWorldState> _departedWorldStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ObjectId, PersistedPlacedCube> _persistedCubes = [];

    // Join-time persisted-player loads by player name (see BeginPersistedPlayerPrefetch): started
    // on receive threads, consumed on the tick.
    private readonly ConcurrentDictionary<string, PersistedPlayerPrefetch> _playerPrefetches = new(StringComparer.OrdinalIgnoreCase);

    // The ports are already resolved by the runtime (an inline write queue and a repository
    // checkpoint writer when only repositories were given); any of them may be null.
    public RuntimePersistenceBridge(
        IRuntimePersistenceHost host,
        IPlayerProfileRepository? playerProfiles,
        IWorldPersistenceRepository? worldPersistence,
        IPersistenceCheckpointWriter? checkpointWriter,
        PersistenceWriteQueue? writes,
        WorldObjectRegistry worldObjects,
        EnemyRegistry enemies,
        SceneEventHub events,
        ObjectIdAllocator objectIds,
        ObjectIdentityMap objectIdentities)
    {
        _host = host;
        _playerProfiles = playerProfiles;
        _worldPersistence = worldPersistence;
        _checkpointWriter = checkpointWriter;
        _writes = writes;
        _worldObjects = worldObjects;
        _enemies = enemies;
        _events = events;
        _objectIds = objectIds;
        _objectIdentities = objectIdentities;
    }

    public PersistenceWriteQueue? Writes => _writes;

    public IPersistenceCheckpointWriter? CheckpointWriter => _checkpointWriter;

    public IWorldPersistenceRepository? WorldPersistence => _worldPersistence;

    // Sessions whose failed join left default stats that must not be saved (tests).
    public int UnrestoredPlayerSessionCount => _unrestoredPlayerSessions.Count;

    // A leaving session's unrestored mark goes with it (DetachRemoteSession).
    public void ForgetUnrestoredSession(PlayerSessionId sessionId) => _unrestoredPlayerSessions.Remove(sessionId);

    // Marks a session unrestored, as a failed synchronous join read does (tests).
    internal void MarkSessionUnrestored(PlayerSessionId sessionId) => _unrestoredPlayerSessions.Add(sessionId);

    // Starts loading a joining player's persisted profile, world state and inventory on the persistence
    // worker, so the join itself (applied on the tick) does no database I/O. Called by the host
    // when the join request arrives (it does not wait for the load); thread-safe. The read is
    // queued behind every write already queued and runs after they commit, so it sees this
    // player's latest saved state, and a newer prefetch for the same name replaces an older one.
    // The load is read-only (TryGet): a join request that is never applied creates no profile row;
    // TryLoadPlayerForRestore creates it when the join is applied. Null when the runtime has
    // no profiles; a prefetch that cannot be queued (the queue is closed) completes as failed.
    public PersistedPlayerPrefetch? BeginPersistedPlayerPrefetch(string playerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        // A join request read while the server shuts down must not restart the drained write queue
        // (the queue itself also refuses work once it is disposed).
        if (_playerProfiles == null || _host.IsDisposed)
        {
            return null;
        }

        IPlayerProfileRepository profiles = _playerProfiles;
        IWorldPersistenceRepository? world = _worldPersistence;
        PersistedPlayerPrefetch prefetch = new(playerName);
        _playerPrefetches[playerName] = prefetch;
        void Load()
        {
            try
            {
                PersistedPlayerProfile? profile = profiles.TryGet(playerName);
                prefetch.Complete(
                    profile,
                    world?.TryGetPlayerWorldState(playerName),
                    world?.LoadPlayerInventory(playerName));
            }
            catch (Exception ex)
            {
                prefetch.Fail(ex);
                NetworkDiagnosticsLog.Write(
                    "server.persistence.prefetch-error",
                    $"player={playerName} {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (_writes != null)
        {
            if (!_writes.EnqueueRead(Load))
            {
                prefetch.Fail(new ObjectDisposedException(nameof(PersistenceWriteQueue), "The persistence queue is closed."));
            }
        }
        else
        {
            Load();
        }

        return prefetch;
    }

    // Tick side of the join prefetch. Ready when TryLoadPlayerForRestore for this name can run
    // without touching the database (no profiles, or the prefetch loaded); Pending while it runs;
    // Failed when it failed; Missing when there is none (another join consumed it, or it was
    // discarded). Reads only memory.
    public PersistedPlayerLoadState GetPersistedPlayerLoadState(string playerName)
    {
        if (_playerProfiles == null)
        {
            return PersistedPlayerLoadState.Ready;
        }

        if (!_playerPrefetches.TryGetValue(playerName, out PersistedPlayerPrefetch? prefetch))
        {
            return PersistedPlayerLoadState.Missing;
        }

        if (!prefetch.IsCompleted)
        {
            return PersistedPlayerLoadState.Pending;
        }

        return prefetch.IsLoaded ? PersistedPlayerLoadState.Ready : PersistedPlayerLoadState.Failed;
    }

    // True when the name's state is Ready. With startIfMissing a name with no prefetch starts one
    // and reports false, so the caller defers the join.
    public bool IsPersistedPlayerReady(string playerName, bool startIfMissing)
    {
        PersistedPlayerLoadState state = GetPersistedPlayerLoadState(playerName);
        if (state == PersistedPlayerLoadState.Missing && startIfMissing)
        {
            BeginPersistedPlayerPrefetch(playerName);
        }

        return state == PersistedPlayerLoadState.Ready;
    }

    // Drops a prefetch whose join was not applied (rejected or released), so a later join loads fresh.
    public void DiscardPersistedPlayerPrefetch(string playerName)
    {
        _playerPrefetches.TryRemove(playerName, out _);
    }

    // The persistence half of WorldSessionRuntime.RestorePersistedPlayerState: resolves the joining
    // player's profile, world state and inventory (from its prefetch, or a synchronous read), marks
    // the session restored and its profile active and cached. The runtime then applies them. False
    // when the runtime has no profiles (nothing to restore). Throws when the synchronous read cannot
    // run because the queued writes did not drain; the session is then left unrestored.
    public bool TryLoadPlayerForRestore(
        PlayerSessionId sessionId,
        string playerName,
        out PersistedPlayerProfile profile,
        out PersistedPlayerWorldState? worldState,
        out PersistedPlayerInventory? inventory)
    {
        inventory = null;
        if (_playerProfiles == null)
        {
            profile = default;
            worldState = null;
            return false;
        }

        if (_playerPrefetches.TryRemove(playerName, out PersistedPlayerPrefetch? prefetch)
            && prefetch.TryGetLoadResult(out PersistedPlayerProfile? prefetched, out PersistedPlayerWorldState? prefetchedWorld))
        {
            // Every profile save this runtime queues goes through SaveProfile, which caches it, so a
            // cached profile is at least as new as the row the prefetch read. That covers a save
            // queued after the prefetch: a reconnect whose old connection is released (and its
            // profile saved) after the new connection asked to join.
            if (_profileCache.TryGetValue(playerName, out PersistedPlayerProfile cached))
            {
                profile = cached;
            }
            else if (prefetched is { } loaded)
            {
                profile = loaded;
            }
            else
            {
                // A new player: the read-only prefetch found no row and the join is now applied, so
                // the row is created here, on the write queue rather than the tick.
                profile = PersistedPlayerProfile.CreateDefault(playerName, DateTime.UtcNow);
                SaveProfile(playerName, profile);
            }

            worldState = prefetchedWorld;
            prefetch.TryGetInventory(out inventory);
        }
        else
        {
            // No loaded prefetch: a direct JoinSession caller (tools, tests, single player). The
            // dedicated host never routes a join before its prefetch loaded, so this never runs on
            // the server tick. Read synchronously after the queued writes. If they do not drain in
            // time the rows may be stale, so the join fails instead (JoinRequestHandler rejects it),
            // and the half-joined player's default stats are not saved over the row on release.
            NetworkDiagnosticsLog.Write("server.persistence.join-sync-read", $"player={playerName}");
            if (_writes != null && !_writes.Flush(TimeSpan.FromSeconds(5)))
            {
                _unrestoredPlayerSessions.Add(sessionId);
                NetworkDiagnosticsLog.Write("server.persistence.join-sync-read-timeout", $"player={playerName}");
                throw new InvalidOperationException(
                    $"Saved data for player '{playerName}' is still being written; retry the join.");
            }

            profile = _playerProfiles.GetOrCreate(playerName);
            worldState = _worldPersistence?.TryGetPlayerWorldState(playerName);
            if (!_host.IsPlayerInventoryLoaded(profile.PlayerName))
            {
                inventory = _worldPersistence?.LoadPlayerInventory(profile.PlayerName);
            }
        }

        // The newest written world state wins: one saved on leave is newer than any row a prefetch read.
        if (_departedWorldStates.Remove(playerName, out PersistedPlayerWorldState departed))
        {
            worldState = departed;
        }

        _unrestoredPlayerSessions.Remove(sessionId);
        MarkProfileActive(playerName);
        CacheProfile(playerName, profile);
        return true;
    }

    public void RestoreWorld()
    {
        if (_worldPersistence == null)
        {
            _host.SpawnInitialWolf();
            return;
        }

        _objectIds.Restore(_worldPersistence.LoadNextObjectId());

        Map map = _host.Map;
        RestoreTerrainOverrides(map);
        // A cube whose cell is later found occupied is skipped but its row still exists, so its id is
        // reserved and the row stays in _persistedCubes. Registered first: the rubble heal below must
        // know every persisted cube id, and it runs before the cubes so a healed cell is free for them.
        PersistedPlacedCube[] persistedCubes = _worldPersistence.LoadPlacedCubes().OrderBy(cube => cube.ObjectId).ToArray();
        foreach (PersistedPlacedCube persistedCube in persistedCubes)
        {
            _objectIds.Reserve(persistedCube.ObjectId);
            _persistedCubes[persistedCube.ObjectId] = persistedCube;
            if (persistedCube.OwnerPlayerName != null)
            {
                _cubeOwnerNames.Add(persistedCube.OwnerPlayerName);
            }
        }

        List<(ObjectId Origin, GeometryCellKey Cell, PersistedCubeFracturePiece[] Pieces)> rubbleGroups =
            PrepareRubbleGroups(map);
        int skippedCubes = 0;
        foreach (PersistedPlacedCube persistedCube in persistedCubes)
        {
            Cube? cube = map.TryAddCube(
                persistedCube.CellPosition,
                materialId: persistedCube.MaterialId);
            if (cube == null)
            {
                skippedCubes++;
                continue;
            }

            if (persistedCube.CenterPosition.HasValue)
            {
                map.UpdateCubePosition(cube, persistedCube.CenterPosition.Value);
            }

            // A fresh actor id: persisted ids are ObjectIds, not actor ids.
            WorldObjectRecord registered = _worldObjects.RegisterCube(
                cube,
                map,
                maxHealth: persistedCube.MaxHealth,
                currentHealth: persistedCube.CurrentHealth,
                actorId: null,
                ownerPlayerName: persistedCube.OwnerPlayerName);
            _objectIdentities.Bind(persistedCube.ObjectId, registered.Actor.Id);
        }

        if (skippedCubes > 0)
        {
            NetworkDiagnosticsLog.Write(
                "server.persistence.restore-skipped-cubes",
                $"count={skippedCubes} reason=cell-occupied-or-out-of-bounds");
        }

        RestoreRubbleGroups(map, rubbleGroups);

        IReadOnlyList<PersistedEnemyInstance> enemies = _worldPersistence.LoadActiveEnemies();
        if (enemies.Count == 0)
        {
            _host.SpawnInitialWolf();
            return;
        }

        _enemies.Clear();
        PersistedEnemyInstance[] ordered = enemies.OrderBy(enemy => enemy.ObjectId).ToArray();
        foreach (PersistedEnemyInstance enemy in ordered)
        {
            _objectIds.Reserve(enemy.ObjectId);
        }

        foreach (PersistedEnemyInstance enemy in ordered)
        {
            // A retired or unknown kind: the row is kept (checkpoints never delete rows) but the
            // enemy is not restored. The population step spawns a wolf when none is active.
            if (!ContentCatalog.Shipped.TryGetEnemy(enemy.EnemyKind, out EnemyTemplate? template))
            {
                NetworkDiagnosticsLog.Write(
                    "server.persistence.restore-skipped-enemy",
                    $"objectId={enemy.ObjectId} kind={enemy.EnemyKind} reason=unknown-enemy-kind");
                continue;
            }

            if (!_host.TryAllocateEnemyActorId(out int actorId))
            {
                // No enemy id left: restore the world without it.
                continue;
            }

            _enemies.Add(CreatePersistedEnemy(enemy, template, actorId));
            _objectIdentities.Bind(enemy.ObjectId, actorId);
        }
    }

    private EnemyActor CreatePersistedEnemy(PersistedEnemyInstance enemy, EnemyTemplate template, int actorId)
    {
        EnemyCombatant combatant = new(
            () => enemy.Position,
            _events,
            actorId: actorId,
            maxHealth: enemy.Stats.MaxHealth,
            attackDamage: Math.Max(1, enemy.Stats.Strength + 2),
            attackIntervalSeconds: Math.Max(0.05f, 1f / Math.Max(0.1f, enemy.Stats.AttackSpeed)),
            anchorHeightOffset: WolfDefinition.CombatAnchorHeight);
        combatant.Health.SetAuthoritativeState(enemy.Stats.CurrentHealth, enemy.Stats.MaxHealth);

        EnemyActor actor = new(
            combatant,
            enemy.Position,
            aggroRange: template.AggroRange,
            attackRange: template.AttackRange,
            moveSpeed: Math.Max(0f, enemy.Stats.MovementSpeed),
            preferredStandOffDistance: WolfDefinition.PreferredStandOffDistance,
            collisionRadius: WolfDefinition.CollisionRadius,
            collisionHeight: WolfDefinition.CollisionHeight,
            displayName: enemy.DisplayName,
            enemyKind: enemy.EnemyKind,
            level: enemy.Stats.Level);
        actor.SetFacing(enemy.Facing);
        return actor;
    }

    public void PersistPlayerProfile(PlayerSessionId sessionId, PlayerCombatant combatant)
    {
        if (_playerProfiles == null
            || !_host.Sessions.TryGet(sessionId, out ConnectedPlayer? player)
            || player == null
            || player.IsLocal
            || _unrestoredPlayerSessions.Contains(sessionId))
        {
            return;
        }

        PersistedPlayerProfile existing = GetCachedProfile(player.DisplayName);
        SaveProfile(player.DisplayName, existing with
        {
            DisplayName = player.DisplayName,
            InteractionMode = _host.GetSessionInteractionMode(sessionId),
            Stats = new PersistedCreatureStats(
                CurrentHealth: combatant.Health.CurrentHealth,
                MaxHealth: combatant.Health.MaxHealth,
                Strength: combatant.BaseStats.Strength,
                Agility: 10,
                Vitality: combatant.BaseStats.Vitality,
                Energy: 10,
                MovementSpeed: 6f,
                AttackSpeed: combatant.BaseStats.AttackSpeed,
                Level: combatant.Level,
                Experience: combatant.Experience)
        });
    }

    // Saves a leaving player's position, facing and HP (player_world_state), queued ahead of its
    // profile save (DetachRemoteSession explains why the order matters), stamped with the leave's
    // time. Without it a leave lost everything since the last periodic checkpoint
    // (checkpoints capture only attached players), so leaving and rejoining healed the player and
    // rolled back their position, and a player who left within one checkpoint interval of first
    // joining had no world-state row at all. The server's shutdown detaches every session before
    // the final checkpoint, so this also covers a shutdown. Called before the session is
    // despawned, while its target still resolves; an unrestored session (default state) is skipped.
    public void PersistPlayerWorldState(PlayerSessionId sessionId)
    {
        if (_playerProfiles == null
            || _worldPersistence == null
            || !_host.Sessions.TryGet(sessionId, out ConnectedPlayer? player)
            || player == null
            || player.IsLocal
            || _unrestoredPlayerSessions.Contains(sessionId)
            || !_host.TryGetPlayerCheckpointState(
                sessionId,
                out Vector3 position,
                out Vector3 facing,
                out int currentHealth,
                out _))
        {
            return;
        }

        PersistedPlayerWorldState state = new(
            player.DisplayName,
            position,
            facing,
            CurrentHealth: currentHealth,
            CurrentEnergy: 0,
            UpdatedUtc: DateTime.UtcNow);
        if (_profileCache.ContainsKey(player.DisplayName))
        {
            // Only names the profile LRU tracks, so EvictDepartedPlayerState bounds this cache.
            _departedWorldStates[player.DisplayName] = state;
        }

        EnqueueWorldWrite(world => world.SavePlayerWorldState(state));
    }

    public void PersistPlacedCube(WorldObjectRecord record, GeometryMaterialId materialId)
    {
        if (_worldPersistence == null || record.SourceObject is not Cube cube)
        {
            return;
        }

        record = RepairCubeRecordFromPersistence(record);
        string? ownerPlayerName = IsValidPersistenceOwnerName(record.OwnerPlayerName) ? record.OwnerPlayerName : null;
        if (!_objectIdentities.TryGetObjectId(record.Actor.Id, out ObjectId objectId))
        {
            // A new placement needs a valid owner to get an ObjectId.
            if (ownerPlayerName == null)
            {
                return;
            }

            objectId = _objectIds.Take();
        }
        else if (ownerPlayerName == null && _persistedCubes.TryGetValue(objectId, out PersistedPlacedCube known))
        {
            ownerPlayerName = known.OwnerPlayerName;
        }

        Vector3 cellPosition = ToCellPosition(cube.Position);
        PersistedPlacedCube persisted = new(
            objectId,
            ownerPlayerName,
            cellPosition,
            GeometryShapeKind.SolidCube,
            materialId,
            record.Health.CurrentHealth,
            record.Health.MaxHealth,
            DateTime.UtcNow,
            DateTime.UtcNow,
            cube.Position);
        _persistedCubes[objectId] = persisted;
        _objectIdentities.Bind(objectId, record.Actor.Id);
        if (ownerPlayerName != null)
        {
            _cubeOwnerNames.Add(ownerPlayerName);
        }

        EnqueueWorldWrite(world => world.SavePlacedCube(persisted));
    }

    // Keeps the in-memory persisted row's health equal to the live record's after a non-fatal
    // hit, so an identity repair never brings back older health. The database row itself is
    // refreshed by the next checkpoint (the hit marks the cube dirty; see TryCaptureCube), not on every hit.
    public void UpdatePersistedCubeHealth(WorldObjectRecord record)
    {
        if (_objectIdentities.TryGetObjectId(record.Actor.Id, out ObjectId objectId) &&
            _persistedCubes.TryGetValue(objectId, out PersistedPlacedCube cached))
        {
            _persistedCubes[objectId] = cached with
            {
                CurrentHealth = record.Health.CurrentHealth,
                MaxHealth = record.Health.MaxHealth
            };
        }
    }

    public void DeletePersistedPlacedCube(int cubeId)
    {
        // A cube with no ObjectId was never persisted (a terrain cube): nothing to delete.
        if (!_objectIdentities.RemoveByActorId(cubeId, out ObjectId objectId))
        {
            return;
        }

        _persistedCubes.Remove(objectId);
        // Tombstones matter only to an incremental capture, and capture tracking starts with the first
        // capture (which is full): a runtime that never captures does not accumulate them. Every
        // capture clears the list.
        if (_worldObjects.IsCheckpointTrackingEnabled)
        {
            _removedCubeObjectIds.Add(objectId);
        }

        _lastRetiredCube = new RetiredCube(cubeId, objectId);
        EnqueueWorldWrite(world => world.DeletePlacedCube(objectId));
    }

    public void EnqueueWorldWrite(Action<IWorldPersistenceRepository> write)
    {
        if (_worldPersistence == null || _writes == null)
        {
            return;
        }

        IWorldPersistenceRepository world = _worldPersistence;
        _writes.Enqueue(() => write(world));
    }

    public PersistedPlayerProfile GetCachedProfile(string playerName)
    {
        if (_profileCache.TryGetValue(playerName, out PersistedPlayerProfile cached))
        {
            return cached;
        }

        // Only reached for a player whose profile was not loaded at join.
        PersistedPlayerProfile loaded = _playerProfiles!.GetOrCreate(playerName);
        CacheProfile(playerName, loaded);
        return loaded;
    }

    private void CacheProfile(string playerName, PersistedPlayerProfile profile)
    {
        _profileCache[playerName] = profile;
        _profileCache[profile.PlayerName] = profile;
        _knownProfileNames.Add(playerName);
        _knownProfileNames.Add(profile.PlayerName);
    }

    // The profile cache keeps every active player's profile and, past that, only the most recently
    // departed players' (an LRU MaxDepartedProfiles long), because a client chooses its player name
    // and a long-running server would otherwise cache one profile per name ever seen. Eviction
    // loses nothing: DetachRemoteSession saved the profile (queued before the eviction) and a later
    // join reads it back through its prefetch or the synchronous fallback, both ordered after the
    // queued writes. An active player's profile is never evicted: GetCachedProfile's fallback
    // reads the repository directly and could miss a save still on the queue.
    public const int DefaultMaxDepartedProfiles = PlayerSessionRegistry.DefaultMaxDepartedSessions;

    private readonly LinkedList<string> _departedProfileNames = new();
    private readonly Dictionary<string, LinkedListNode<string>> _departedProfileNodes = new(StringComparer.OrdinalIgnoreCase);

    // Settable for tests.
    public int MaxDepartedProfiles { get; set; } = DefaultMaxDepartedProfiles;

    public int ProfileCacheCount => _profileCache.Count;

    public int DepartedProfileCount => _departedProfileNames.Count;

    // The name's player is attached again: its profile leaves the eviction order.
    public void MarkProfileActive(string playerName)
    {
        if (_departedProfileNodes.Remove(playerName, out LinkedListNode<string>? node))
        {
            _departedProfileNames.Remove(node);
        }
    }

    // The name's last active session left: its cached profile becomes the newest eviction candidate.
    public void MarkProfileDeparted(string playerName)
    {
        if (!_profileCache.ContainsKey(playerName) || IsProfileNameActive(playerName))
        {
            return;
        }

        MarkProfileActive(playerName);
        _departedProfileNodes[playerName] = _departedProfileNames.AddLast(playerName);
        while (_departedProfileNames.Count > MaxDepartedProfiles)
        {
            string oldest = _departedProfileNames.First!.Value;
            _departedProfileNames.RemoveFirst();
            _departedProfileNodes.Remove(oldest);
            EvictCachedProfile(oldest);
        }
    }

    // Also evicts the name's inventory and its _knownProfileNames entry. Inventory is written
    // through (every change queues its write when it happens, see PlayerInventoryService.PersistSlot), and a later
    // join reads the queued writes back through its prefetch (ordered behind them) or the
    // synchronous fallback (which flushes them first). The queue drops a write that fails, though,
    // so an inventory whose writes may have failed is kept (or restored on the next load) instead
    // of being replaced by stale rows; see EvictPlayerInventory.
    private void EvictCachedProfile(string playerName)
    {
        if (_profileCache.Remove(playerName, out PersistedPlayerProfile profile)
            && !string.Equals(profile.PlayerName, playerName, StringComparison.OrdinalIgnoreCase)
            && !IsProfileNameActive(profile.PlayerName))
        {
            // CacheProfile also stores the profile under the repository's own name for it.
            _profileCache.Remove(profile.PlayerName);
            EvictDepartedPlayerState(profile.PlayerName);
        }

        EvictDepartedPlayerState(playerName);

        // A prefetch already loaded for this name may predate the save the cache held; with the
        // cache entry gone a join would apply it, so drop it and let the join load afresh.
        _playerPrefetches.TryRemove(playerName, out _);
    }

    private void EvictDepartedPlayerState(string playerName)
    {
        _knownProfileNames.Remove(playerName);
        _departedWorldStates.Remove(playerName);
        _host.EvictPlayerInventory(playerName);
    }

    // Names that own a persisted cube (restored or placed this run). IsValidPersistenceOwnerName
    // accepts them without a repository read; unlike _knownProfileNames (cached profile names,
    // evicted with the profile LRU) this grows only with cube placements, not with every join.
    private readonly HashSet<string> _cubeOwnerNames = new(StringComparer.OrdinalIgnoreCase);

    public int KnownProfileNameCount => _knownProfileNames.Count;

    private bool IsProfileNameActive(string playerName)
    {
        foreach (ConnectedPlayer player in _host.Sessions.Players)
        {
            if (!player.IsLocal
                && player.IsSpawned
                && string.Equals(player.DisplayName, playerName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void SaveProfile(string playerName, PersistedPlayerProfile profile)
    {
        CacheProfile(playerName, profile);
        IPlayerProfileRepository profiles = _playerProfiles!;
        _writes?.Enqueue(() => profiles.Save(profile));
    }

    // Loads the rubble groups, prunes the stale ones and heals lost terrain overrides; returns the kept
    // groups in origin order. Runs before the placed cubes are restored (see RestoreWorld).
    private List<(ObjectId Origin, GeometryCellKey Cell, PersistedCubeFracturePiece[] Pieces)> PrepareRubbleGroups(Map map)
    {
        // Groups by origin, then one per cell: a cell's rubble can have several groups in the
        // database (rows orphaned before wave D); only the newest is the live one. Newest means the
        // highest piece ObjectId: piece ids are taken at fracture time, so they follow fracture order
        // (an origin is a placement id, and a moved cube broken later can have a lower one).
        List<(ObjectId Origin, GeometryCellKey Cell, PersistedCubeFracturePiece[] Pieces)> groups = [];
        foreach (IGrouping<ObjectId, PersistedCubeFracturePiece> group in _worldPersistence
            .LoadCubeFracturePieces()
            .GroupBy(piece => piece.OriginObjectId)
            .OrderBy(group => group.Key))
        {
            // The fractured cube's row is gone, but its id still keys these pieces and is never reissued.
            _objectIds.Reserve(group.Key);
            PersistedCubeFracturePiece[] pieces = group
                .OrderBy(piece => piece.PieceIndex)
                .ToArray();
            foreach (PersistedCubeFracturePiece piece in pieces)
            {
                _objectIds.Reserve(piece.ObjectId);
            }

            if (pieces.Length > 0)
            {
                groups.Add((group.Key, ToGeometryCellKey(pieces[0].ParentCell), pieces));
            }
        }

        Dictionary<GeometryCellKey, (ObjectId MaxPiece, ObjectId Origin)> newest = [];
        foreach ((ObjectId groupOrigin, GeometryCellKey groupCell, PersistedCubeFracturePiece[] groupPieces) in groups)
        {
            ObjectId maxPiece = groupPieces[0].ObjectId;
            foreach (PersistedCubeFracturePiece piece in groupPieces)
            {
                if (piece.ObjectId.CompareTo(maxPiece) > 0)
                {
                    maxPiece = piece.ObjectId;
                }
            }

            // Ties (impossible for distinct piece ids) fall back to the higher origin.
            if (!newest.TryGetValue(groupCell, out (ObjectId MaxPiece, ObjectId Origin) known) ||
                maxPiece.CompareTo(known.MaxPiece) > 0 ||
                (maxPiece.CompareTo(known.MaxPiece) == 0 && groupOrigin.CompareTo(known.Origin) > 0))
            {
                newest[groupCell] = (maxPiece, groupOrigin);
            }
        }

        List<(ObjectId Origin, GeometryCellKey Cell, PersistedCubeFracturePiece[] Pieces)> kept = [];
        int pruned = 0;
        int healed = 0;
        foreach ((ObjectId groupOrigin, GeometryCellKey cell, PersistedCubeFracturePiece[] pieces) in groups)
        {
            if (newest[cell].Origin != groupOrigin)
            {
                pruned++;
                ObjectId stale = groupOrigin;
                EnqueueWorldWrite(world => world.DeleteCubeFracturePieces(stale));
                continue;
            }

            // Heal: rubble over a cell that is still a source cell, whose origin is neither an override
            // nor a placed cube, means the Removed override row was lost (crash or deletion). Remove
            // the cell as the override would have, and write the row back under the reserved origin id.
            // A cell that already has an override row (for example an unsupported kind) is never healed.
            if (map.IsSourceCell(cell) &&
                !_persistedTerrainOverrides.Contains(cell) &&
                !_persistedTerrainOverrideIds.Contains(groupOrigin) &&
                !_persistedCubes.ContainsKey(groupOrigin) &&
                map.RemoveSourceCell(cell))
            {
                healed++;
                _persistedTerrainOverrides.Add(cell);
                _persistedTerrainOverrideIds.Add(groupOrigin);
                DateTime now = DateTime.UtcNow;
                PersistedTerrainCellOverride healedOverride = new(groupOrigin, cell, TerrainCellOverrideKind.Removed, null, now, now);
                EnqueueWorldWrite(world => world.SaveTerrainCellOverride(healedOverride));
            }

            kept.Add((groupOrigin, cell, pieces));
        }

        if (pruned > 0)
        {
            NetworkDiagnosticsLog.Write("server.persistence.orphan-rubble-pruned", $"count={pruned}");
        }

        if (healed > 0)
        {
            NetworkDiagnosticsLog.Write("server.persistence.terrain-override-healed", $"count={healed}");
        }

        return kept;
    }

    private void RestoreRubbleGroups(
        Map map,
        List<(ObjectId Origin, GeometryCellKey Cell, PersistedCubeFracturePiece[] Pieces)> groups)
    {
        foreach ((ObjectId _, GeometryCellKey cell, PersistedCubeFracturePiece[] pieces) in groups)
        {
            PersistedCubeFracturePiece first = pieces[0];
            GeometryFracturePiece[] fracturePieces = pieces
                .Select(piece => new GeometryFracturePiece(piece.Center, piece.SideLength))
                .ToArray();

            if (map.RestoreFracturePieces(cell, first.MaterialId, fracturePieces, withPhysics: true))
            {
                // The map keeps only the valid pieces, renumbered in order; PieceIds follows it.
                List<ObjectId> pieceIds = new(pieces.Length);
                for (int i = 0; i < pieces.Length; i++)
                {
                    if (fracturePieces[i].IsValid())
                    {
                        pieceIds.Add(pieces[i].ObjectId);
                    }
                }

                _persistedFractureGroups[cell] = new PersistedFractureGroup(
                    first.OriginObjectId,
                    first.OwnerPlayerName,
                    pieceIds.ToArray());
            }
        }
    }

    // A base-terrain cell was destroyed: persists a Removed override under a fresh ObjectId, once
    // per cell, so the cell stays destroyed after a restart. The id is also the origin of the rubble
    // the resolver persists next (PersistFracturePieces), which finds it as the retired cube.
    public void PersistTerrainCellRemoved(WorldObjectRecord record, GeometryCellKey cell)
    {
        if (_worldPersistence == null || !_persistedTerrainOverrides.Add(cell))
        {
            return;
        }

        ObjectId id = _objectIds.Take();
        DateTime now = DateTime.UtcNow;
        PersistedTerrainCellOverride cellOverride = new(id, cell, TerrainCellOverrideKind.Removed, null, now, now);
        _persistedTerrainOverrideIds.Add(id);
        EnqueueWorldWrite(world => world.SaveTerrainCellOverride(cellOverride));
        _lastRetiredCube = new RetiredCube(record.Actor.Id, id);
    }

    // Applies the persisted terrain overrides to the freshly built map, in object_id order and
    // before any placed cube, rubble or enemy, through the live removal path (Map.RemoveSourceCell).
    // A row that cannot apply (not a source cell now, or a kind this build does not produce) keeps
    // its row and its reserved id.
    private void RestoreTerrainOverrides(Map map)
    {
        if (_worldPersistence == null)
        {
            return;
        }

        int skipped = 0;
        foreach (PersistedTerrainCellOverride cellOverride in _worldPersistence
            .LoadTerrainCellOverrides()
            .OrderBy(row => row.ObjectId))
        {
            _objectIds.Reserve(cellOverride.ObjectId);
            _persistedTerrainOverrides.Add(cellOverride.Cell);
            _persistedTerrainOverrideIds.Add(cellOverride.ObjectId);
            if (cellOverride.Kind != TerrainCellOverrideKind.Removed)
            {
                NetworkDiagnosticsLog.Write(
                    "server.persistence.terrain-override-unsupported",
                    $"object={cellOverride.ObjectId.Value} kind={cellOverride.Kind}");
                continue;
            }

            if (!map.RemoveSourceCell(cellOverride.Cell))
            {
                skipped++;
            }
        }

        if (skipped > 0)
        {
            NetworkDiagnosticsLog.Write(
                "server.persistence.terrain-override-skipped",
                $"count={skipped} reason=not-a-source-cell");
        }
    }

    // Persists the rubble a destroyed cube left in cell, one new ObjectId per piece. The origin is
    // the cube's ObjectId: its binding when the cube is still live, else the cube
    // DeletePersistedPlacedCube just retired (the resolver deletes, then fractures, in one tick).
    public void PersistFracturePieces(WorldObjectRecord record, GeometryCellKey cell)
    {
        if (_worldPersistence == null)
        {
            return;
        }

        // A cell fractured before (a cube placed over rubble, then broken) replaces the old group in
        // the map: its rows would be orphans, so they go now and the next incremental capture
        // carries the tombstone. The early returns below also drop it: the map replaced the group
        // whether or not the new one can be persisted (only reached for an actual new fracture).
        if (record.SourceObject is not Cube)
        {
            DropReplacedFractureGroup(cell, newOrigin: null);
            return;
        }

        string? ownerPlayerName = IsValidPersistenceOwnerName(record.OwnerPlayerName) ? record.OwnerPlayerName : null;
        if (!_objectIdentities.TryGetObjectId(record.Actor.Id, out ObjectId origin))
        {
            if (_lastRetiredCube.ActorId == record.Actor.Id && !_lastRetiredCube.ObjectId.IsNone)
            {
                origin = _lastRetiredCube.ObjectId;
            }
            else if (ownerPlayerName != null)
            {
                origin = _objectIds.Take();
            }
            else
            {
                DropReplacedFractureGroup(cell, newOrigin: null);
                return;
            }
        }

        DropReplacedFractureGroup(cell, origin);

        if (ownerPlayerName == null && _persistedCubes.TryGetValue(origin, out PersistedPlacedCube known))
        {
            ownerPlayerName = known.OwnerPlayerName;
        }

        _lastRetiredCube = default;

        // The one cell's pieces in index order: the same rows a filtered whole-map scan yields, without
        // walking every rubble cell on each fracture.
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> cellPieces =
            _fracturePieceCellScratch;
        cellPieces.Clear();
        _host.Map.CaptureFracturePiecesInto(cell, cellPieces);
        ObjectId[] pieceIds = new ObjectId[cellPieces.Count];
        for (int i = 0; i < cellPieces.Count; i++)
        {
            (GeometryCellKey PieceCell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId) = cellPieces[i];
            ObjectId pieceId = _objectIds.Take();
            // PieceIds is indexed by map piece index; a fresh fracture's pieces are valid and 0..n-1.
            if (PieceIndex < pieceIds.Length)
            {
                pieceIds[PieceIndex] = pieceId;
            }

            PersistedCubeFracturePiece persistedPiece = new(
                ObjectId: pieceId,
                OriginObjectId: origin,
                PieceIndex: PieceIndex,
                OwnerPlayerName: ownerPlayerName,
                ParentCell: new Vector3(PieceCell.X, PieceCell.Y, PieceCell.Z),
                Center: Piece.Center,
                SideLength: Piece.SideLength,
                MaterialId: MaterialId,
                CreatedUtc: DateTime.UtcNow,
                UpdatedUtc: DateTime.UtcNow);
            EnqueueWorldWrite(world => world.SaveCubeFracturePiece(persistedPiece));
        }

        cellPieces.Clear();
        _persistedFractureGroups[cell] = new PersistedFractureGroup(origin, ownerPlayerName, pieceIds);
    }

    private void DropReplacedFractureGroup(GeometryCellKey cell, ObjectId? newOrigin)
    {
        if (!_persistedFractureGroups.Remove(cell, out PersistedFractureGroup previous) ||
            (newOrigin.HasValue && previous.Origin == newOrigin.Value))
        {
            return;
        }

        ObjectId oldOrigin = previous.Origin;
        EnqueueWorldWrite(world => world.DeleteCubeFracturePieces(oldOrigin));
        if (_worldObjects.IsCheckpointTrackingEnabled)
        {
            _removedFractureOriginIds.Add(oldOrigin);
        }
    }

    // Reused by PersistFracturePieces (tick thread only).
    private readonly List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)>
        _fracturePieceCellScratch = [];

    // A new enemy is a new object, even a respawn: it takes a fresh ObjectId and is bound to its
    // actor id. Without world persistence nothing is allocated or written.
    public void PersistEnemy(EnemyActor enemy)
    {
        if (_worldPersistence == null)
        {
            return;
        }

        ObjectId objectId = _objectIds.Take();
        _objectIdentities.Bind(objectId, enemy.Combatant.Actor.Id);
        PersistedEnemyInstance persisted = CreatePersistedEnemy(enemy, objectId, DateTime.UtcNow);
        EnqueueWorldWrite(world => world.SaveEnemyInstance(persisted));
    }

    private static PersistedEnemyInstance CreatePersistedEnemy(EnemyActor enemy, ObjectId objectId, DateTime now)
    {
        return new PersistedEnemyInstance(
            objectId,
            enemy.EnemyKind,
            enemy.DisplayName,
            enemy.Position,
            enemy.Forward,
            new PersistedCreatureStats(
                CurrentHealth: enemy.Combatant.Health.CurrentHealth,
                MaxHealth: enemy.Combatant.Health.MaxHealth,
                Strength: 10,
                Agility: 10,
                Vitality: 10,
                Energy: 0,
                MovementSpeed: enemy.MoveSpeed,
                AttackSpeed: 1f / Math.Max(0.05f, enemy.Combatant.AttackIntervalSeconds),
                Level: enemy.Level),
            now,
            now);
    }

    // A no-op for an enemy with no ObjectId (never persisted).
    public void DeletePersistedEnemy(int enemyId)
    {
        if (!_objectIdentities.RemoveByActorId(enemyId, out ObjectId objectId))
        {
            return;
        }

        EnqueueWorldWrite(world => world.DeleteEnemyInstance(objectId));
    }

    // PieceIds is indexed by the map's piece index; a cell's piece list is fixed once it fractures.
    private readonly record struct PersistedFractureGroup(
        ObjectId Origin,
        string? OwnerPlayerName,
        ObjectId[] PieceIds);

    private readonly record struct RetiredCube(int ActorId, ObjectId ObjectId);

    public WorldObjectRecord RepairCubeRecordFromPersistence(WorldObjectRecord record)
    {
        if (_worldPersistence == null ||
            record.SourceObject is not Cube cube ||
            !TryResolvePersistedPlacedCube(cube, out PersistedPlacedCube persistedCube))
        {
            return record;
        }

        // Only identity (id and owner) is repaired: health is live state that combat changes
        // between checkpoints, so a record whose identity still matches keeps its own health.
        // When the registry link was lost (the resolver registered a fresh record with a new id
        // and no owner), the record's health is meaningless and the cached row's health, kept
        // current by every hit (UpdatePersistedCubeHealth), is restored with the identity.
        bool identityMatches =
            _objectIdentities.TryGetObjectId(record.Actor.Id, out ObjectId bound) &&
            bound == persistedCube.ObjectId &&
            string.Equals(record.OwnerPlayerName, persistedCube.OwnerPlayerName, StringComparison.OrdinalIgnoreCase);
        if (identityMatches)
        {
            return record;
        }

        int actorId = _objectIdentities.TryGetActorId(persistedCube.ObjectId, out int boundActorId)
            ? boundActorId
            : record.Actor.Id;
        WorldObjectRecord repaired = _worldObjects.ReplaceCubeRecordIdentity(
            cube,
            _host.Map,
            actorId,
            persistedCube.MaxHealth,
            persistedCube.CurrentHealth,
            persistedCube.OwnerPlayerName);
        _objectIdentities.Bind(persistedCube.ObjectId, repaired.Actor.Id);
        return repaired;
    }

    private bool TryResolvePersistedPlacedCube(Cube cube, out PersistedPlacedCube persistedCube)
    {
        persistedCube = default;
        if (_worldPersistence == null)
        {
            return false;
        }

        // In-memory index of persisted rows (loaded at startup, kept current by saves, deletes
        // and checkpoints) instead of reading the whole table on every hit and placement.
        if (_persistedCubes.Count == 0)
        {
            return false;
        }

        GeometryCellKey currentCell = ToGeometryCellKey(cube.Position);
        PersistedPlacedCube[] exactCellMatches = _persistedCubes.Values
            .Where(candidate => ToGeometryCellKey(candidate.CellPosition) == currentCell)
            .ToArray();
        if (exactCellMatches.Length == 1)
        {
            persistedCube = exactCellMatches[0];
            return true;
        }

        return false;
    }

    private bool IsValidPersistenceOwnerName(string? ownerPlayerName)
    {
        if (string.IsNullOrWhiteSpace(ownerPlayerName))
        {
            return false;
        }

        if (_playerProfiles == null
            || _knownProfileNames.Contains(ownerPlayerName)
            || _cubeOwnerNames.Contains(ownerPlayerName))
        {
            return true;
        }

        // Rare: a cube owner whose profile is not cached and who owns no persisted cube. Cache a
        // positive answer with the cube owners (this is only asked about cube records' owners).
        if (_playerProfiles.TryGet(ownerPlayerName) != null)
        {
            _cubeOwnerNames.Add(ownerPlayerName);
            return true;
        }

        return false;
    }

    private static GeometryCellKey ToGeometryCellKey(Vector3 position)
    {
        return new GeometryCellKey(
            (int)MathF.Floor(position.X + 1e-6f),
            (int)MathF.Floor(position.Y + 1e-6f),
            (int)MathF.Floor(position.Z + 1e-6f));
    }

    // Same cell snapping as the runtime's replication (WorldSessionRuntime.ToCellPosition).
    private static Vector3 ToCellPosition(Vector3 position)
    {
        return new Vector3(
            MathF.Floor(position.X + 1e-6f),
            MathF.Floor(position.Y + 1e-6f),
            MathF.Floor(position.Z + 1e-6f));
    }
}
