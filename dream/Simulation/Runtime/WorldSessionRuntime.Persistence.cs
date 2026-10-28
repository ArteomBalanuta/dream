using System.Collections.Concurrent;
using OpenTK.Mathematics;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Persistence;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Runtime;

// Persistence: persisted player prefetch and restore, world restore, and the cube, fracture,
// enemy and profile write-through helpers.
public sealed partial class WorldSessionRuntime
{
    // Join-time persisted-player loads by player name (see BeginPersistedPlayerPrefetch): started
    // on receive threads, consumed on the tick.
    private readonly ConcurrentDictionary<string, PersistedPlayerPrefetch> _playerPrefetches = new(StringComparer.OrdinalIgnoreCase);

    // Starts loading a joining player's persisted profile and world state on the persistence
    // worker, so the join itself (applied on the tick) does no database I/O. Called by the host
    // when the join request arrives (it does not wait for the load); thread-safe. The read is
    // queued behind every write already queued and runs after they commit, so it sees this
    // player's latest saved state, and a newer prefetch for the same name replaces an older one.
    // The load is read-only (TryGet): a join request that is never applied creates no profile row;
    // RestorePersistedPlayerState creates it when the join is applied. Null when the runtime has
    // no profiles; a prefetch that cannot be queued (the queue is closed) completes as failed.
    public PersistedPlayerPrefetch? BeginPersistedPlayerPrefetch(string playerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        // A join request read while the server shuts down must not restart the drained write queue
        // (the queue itself also refuses work once it is disposed).
        if (_playerProfiles == null || Volatile.Read(ref _disposed))
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
                prefetch.Complete(profile, world?.TryGetPlayerWorldState(playerName));
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

    // Tick side of the join prefetch. Ready when RestorePersistedPlayerState for this name can run
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

    public void RestorePersistedPlayerState(PlayerSessionId sessionId, string playerName)
    {
        if (_playerProfiles == null)
        {
            return;
        }

        PersistedPlayerProfile profile;
        PersistedPlayerWorldState? worldState;
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
        }

        _unrestoredPlayerSessions.Remove(sessionId);
        CacheProfile(playerName, profile);
        ApplyPersistedPlayerState(sessionId, profile, worldState);
        EnsurePlayerInventory(profile.PlayerName);
        RefreshAuthoritativePlayerCombatStats(sessionId);
    }

    public PersistenceWriteQueue? PersistenceWrites => _writes;

    // Sessions whose failed join left default stats that must not be saved (tests).
    internal int UnrestoredPlayerSessionCount => _unrestoredPlayerSessions.Count;

    public void ApplyPersistedPlayerState(
        PlayerSessionId sessionId,
        PersistedPlayerProfile profile,
        PersistedPlayerWorldState? worldState)
    {
        SetSessionInteractionMode(sessionId, profile.InteractionMode);
        if (!TryResolveAuthoritativePlayerTarget(sessionId, out _, out IAuthoritativePlayerTarget? playerTarget)
            || playerTarget == null)
        {
            return;
        }

        playerTarget.Combatant.ApplyBaseStats(new PlayerCombatStats
        {
            Strength = profile.Stats.Strength,
            AttackSpeed = profile.Stats.AttackSpeed,
            Vitality = profile.Stats.Vitality
        });
        playerTarget.Combatant.ApplyProgression(profile.Stats.Level, profile.Stats.Experience);

        Vector3 persistedPosition = worldState?.Position ?? playerTarget.Position;
        Vector3 persistedFacing = worldState?.Facing ?? playerTarget.Facing;
        int currentHealth = worldState?.CurrentHealth ?? profile.Stats.CurrentHealth;
        int maxHealth = playerTarget.Combatant.DerivedStats.MaxHealth;

        playerTarget.SetWorldPosition(persistedPosition);
        if (sessionId == ResolvePrimaryControlledSessionId())
        {
            Player.SetFacing(persistedFacing);
            Player.SetHealth(currentHealth, maxHealth);
        }
        else if (_secondaryRemotePlayers.TryGetValue(sessionId, out RemoteSessionPlayerState? remote))
        {
            remote.SetFacing(persistedFacing);
            remote.SetHealth(currentHealth, maxHealth);
        }
    }

    private void RestorePersistedWorldState()
    {
        if (_worldPersistence == null)
        {
            SpawnInitialWolf();
            return;
        }

        RestorePersistedInventoryState();

        foreach (PersistedPlacedCube persistedCube in _worldPersistence.LoadPlacedCubes())
        {
            _persistedCubes[persistedCube.CubeId] = persistedCube;
            _knownProfileNames.Add(persistedCube.OwnerPlayerName);
            // The id counters are derived from the persisted ids (there is no stored counter): a
            // cube id is reserved even when its cell is now occupied and the cube is skipped,
            // because its row still exists. A legacy cube id in the enemy range is reserved there.
            _worldObjects.ReserveActorId(persistedCube.CubeId);
            ReserveEnemyActorId(persistedCube.CubeId);
            Cube? cube = Map.TryAddCube(
                persistedCube.CellPosition,
                materialId: persistedCube.MaterialId);
            if (cube == null)
            {
                continue;
            }

            if (persistedCube.CenterPosition.HasValue)
            {
                Map.UpdateCubePosition(cube, persistedCube.CenterPosition.Value);
            }

            _worldObjects.RegisterCube(
                cube,
                Map,
                maxHealth: persistedCube.MaxHealth,
                currentHealth: persistedCube.CurrentHealth,
                actorId: persistedCube.CubeId,
                ownerPlayerName: persistedCube.OwnerPlayerName);
        }

        RestorePersistedCubeFracturePieces();

        IReadOnlyList<PersistedEnemyInstance> enemies = _worldPersistence.LoadActiveEnemies();
        if (enemies.Count == 0)
        {
            SpawnInitialWolf();
            return;
        }

        _enemies.Clear();
        // A restored world never hands a later spawn an id it already persisted. Every persisted
        // id is reserved before any enemy is re-identified below, so a new id skips them all.
        foreach (PersistedEnemyInstance enemy in enemies)
        {
            ReserveEnemyActorId(enemy.EnemyId);
        }

        foreach (PersistedEnemyInstance enemy in enemies)
        {
            PersistedEnemyInstance restored = enemy;
            if (_persistedCubes.ContainsKey(enemy.EnemyId))
            {
                // A world saved before the id ranges were partitioned can hold an enemy and a
                // placed cube under one id. The cube keeps it; the enemy is re-identified in the
                // enemy range and its row moved to the new id. The move is one queued write (one
                // UPDATE, under one savepoint), so a failure or crash leaves the row under exactly
                // one id: the old one, re-identified again on the next restore, or the new one.
                if (!TryAllocateEnemyActorId(out int newEnemyId))
                {
                    // No enemy id left: leave the row as it is and restore the world without it.
                    continue;
                }

                int oldEnemyId = enemy.EnemyId;
                restored = enemy with { EnemyId = newEnemyId };
                EnqueueWorldWrite(world => world.ReassignEnemyId(oldEnemyId, newEnemyId));
            }

            _enemies.Add(CreatePersistedEnemy(restored));
        }
    }

    private EnemyActor CreatePersistedEnemy(PersistedEnemyInstance enemy)
    {
        EnemyCombatant combatant = new(
            () => enemy.Position,
            _events,
            actorId: enemy.EnemyId,
            maxHealth: enemy.Stats.MaxHealth,
            attackDamage: Math.Max(1, enemy.Stats.Strength + 2),
            attackIntervalSeconds: Math.Max(0.05f, 1f / Math.Max(0.1f, enemy.Stats.AttackSpeed)),
            anchorHeightOffset: WolfDefinition.CombatAnchorHeight);
        combatant.Health.SetAuthoritativeState(enemy.Stats.CurrentHealth, enemy.Stats.MaxHealth);

        EnemyActor actor = new(
            combatant,
            enemy.Position,
            aggroRange: 10f,
            attackRange: 1.35f,
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

    private void PersistPlayerProfile(PlayerSessionId sessionId, PlayerCombatant combatant)
    {
        if (_playerProfiles == null
            || !_sessions.TryGet(sessionId, out ConnectedPlayer? player)
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
            InteractionMode = GetSessionInteractionMode(sessionId),
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

    private void PersistPlacedCube(WorldObjectRecord record, GeometryMaterialId materialId)
    {
        if (_worldPersistence == null || record.SourceObject is not Cube cube)
        {
            return;
        }

        record = RepairCubeRecordFromPersistence(record);
        if (!TryResolveCubePersistenceIdentity(record, cube, out int cubeId, out string ownerPlayerName))
        {
            return;
        }

        Vector3 cellPosition = ToCellPosition(cube.Position);
        PersistedPlacedCube persisted = new(
            cubeId,
            ownerPlayerName,
            cellPosition,
            GeometryShapeKind.SolidCube,
            materialId,
            record.Health.CurrentHealth,
            record.Health.MaxHealth,
            DateTime.UtcNow,
            DateTime.UtcNow,
            cube.Position);
        _persistedCubes[cubeId] = persisted;
        EnqueueWorldWrite(world =>
        {
            world.DeleteCubeFracturePieces(cubeId);
            world.SavePlacedCube(persisted);
        });
    }

    // Keeps the in-memory persisted row's health equal to the live record's after a non-fatal
    // hit, so an identity repair never brings back older health. The database row itself is
    // refreshed by the next checkpoint (the hit marks the cube dirty; see TryCaptureCube), not on every hit.
    private void UpdatePersistedCubeHealth(WorldObjectRecord record)
    {
        if (_persistedCubes.TryGetValue(record.Actor.Id, out PersistedPlacedCube cached))
        {
            _persistedCubes[record.Actor.Id] = cached with
            {
                CurrentHealth = record.Health.CurrentHealth,
                MaxHealth = record.Health.MaxHealth
            };
        }
    }

    private void DeletePersistedPlacedCube(int cubeId)
    {
        _persistedCubes.Remove(cubeId);
        EnqueueWorldWrite(world => world.DeletePlacedCube(cubeId));
    }

    private void EnqueueWorldWrite(Action<IWorldPersistenceRepository> write)
    {
        if (_worldPersistence == null || _writes == null)
        {
            return;
        }

        IWorldPersistenceRepository world = _worldPersistence;
        _writes.Enqueue(() => write(world));
    }

    private PersistedPlayerProfile GetCachedProfile(string playerName)
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

    private void SaveProfile(string playerName, PersistedPlayerProfile profile)
    {
        CacheProfile(playerName, profile);
        IPlayerProfileRepository profiles = _playerProfiles!;
        _writes?.Enqueue(() => profiles.Save(profile));
    }

    private void RestorePersistedCubeFracturePieces()
    {
        if (_worldPersistence == null)
        {
            return;
        }

        foreach (IGrouping<int, PersistedCubeFracturePiece> group in _worldPersistence
            .LoadCubeFracturePieces()
            .GroupBy(piece => piece.SourceCubeId))
        {
            // The fractured cube's row is gone, but its id still keys these pieces: a placed cube
            // that later reused it would delete them (PersistPlacedCube clears its id's pieces).
            _worldObjects.ReserveActorId(group.Key);
            PersistedCubeFracturePiece[] pieces = group
                .OrderBy(piece => piece.PieceIndex)
                .ToArray();
            if (pieces.Length == 0)
            {
                continue;
            }

            PersistedCubeFracturePiece first = pieces[0];
            GeometryCellKey cell = new(
                (int)MathF.Floor(first.ParentCell.X + 1e-6f),
                (int)MathF.Floor(first.ParentCell.Y + 1e-6f),
                (int)MathF.Floor(first.ParentCell.Z + 1e-6f));
            GeometryFracturePiece[] fracturePieces = pieces
                .Select(piece => new GeometryFracturePiece(piece.Center, piece.SideLength))
                .ToArray();

            if (Map.RestoreFracturePieces(cell, first.MaterialId, fracturePieces, withPhysics: true))
            {
                _persistedFractureGroups[cell] = new PersistedFractureGroup(
                    first.SourceCubeId,
                    first.OwnerPlayerName);
            }
        }
    }

    private void PersistFracturePieces(WorldObjectRecord record, GeometryCellKey cell)
    {
        if (_worldPersistence == null)
        {
            return;
        }

        if (record.SourceObject is not Cube cube ||
            !TryResolveCubePersistenceIdentity(record, cube, out int sourceCubeId, out string ownerPlayerName))
        {
            return;
        }

        _persistedFractureGroups[cell] = new PersistedFractureGroup(sourceCubeId, ownerPlayerName);
        PersistFracturePiecesForCell(cell);
    }

    private void PersistFracturePiecesForCell(GeometryCellKey cell)
    {
        if (_worldPersistence == null ||
            !_persistedFractureGroups.TryGetValue(cell, out PersistedFractureGroup group))
        {
            return;
        }

        foreach ((GeometryCellKey PieceCell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId) in
            Map.CaptureFracturePieces().Where(piece => piece.Cell == cell))
        {
            PersistedCubeFracturePiece persistedPiece = new(
                SourceCubeId: group.SourceCubeId,
                PieceIndex: PieceIndex,
                OwnerPlayerName: group.OwnerPlayerName,
                ParentCell: new Vector3(PieceCell.X, PieceCell.Y, PieceCell.Z),
                Center: Piece.Center,
                SideLength: Piece.SideLength,
                MaterialId: MaterialId,
                CreatedUtc: DateTime.UtcNow,
                UpdatedUtc: DateTime.UtcNow);
            EnqueueWorldWrite(world => world.SaveCubeFracturePiece(persistedPiece));
        }
    }

    private void PersistEnemy(EnemyActor enemy)
    {
        PersistedEnemyInstance persisted = CreatePersistedEnemy(enemy, DateTime.UtcNow);
        EnqueueWorldWrite(world => world.SaveEnemyInstance(persisted));
    }

    private static PersistedEnemyInstance CreatePersistedEnemy(EnemyActor enemy, DateTime now)
    {
        return new PersistedEnemyInstance(
            enemy.Combatant.Actor.Id,
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

    private void DeletePersistedEnemy(int enemyId)
    {
        EnqueueWorldWrite(world => world.DeleteEnemyInstance(enemyId));
    }

    private readonly record struct PersistedFractureGroup(
        int SourceCubeId,
        string OwnerPlayerName);

    private WorldObjectRecord RepairCubeRecordFromPersistence(WorldObjectRecord record)
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
            record.Actor.Id == persistedCube.CubeId &&
            string.Equals(record.OwnerPlayerName, persistedCube.OwnerPlayerName, StringComparison.OrdinalIgnoreCase);
        if (identityMatches)
        {
            return record;
        }

        return _worldObjects.ReplaceCubeRecordIdentity(
            cube,
            Map,
            persistedCube.CubeId,
            persistedCube.MaxHealth,
            persistedCube.CurrentHealth,
            persistedCube.OwnerPlayerName);
    }

    private bool TryResolveCubePersistenceIdentity(
        WorldObjectRecord record,
        Cube cube,
        out int cubeId,
        out string ownerPlayerName)
    {
        if (IsValidPersistenceOwnerName(record.OwnerPlayerName))
        {
            cubeId = record.Actor.Id;
            ownerPlayerName = record.OwnerPlayerName!;
            return true;
        }

        if (TryResolvePersistedPlacedCube(cube, out PersistedPlacedCube persistedCube))
        {
            cubeId = persistedCube.CubeId;
            ownerPlayerName = persistedCube.OwnerPlayerName;
            return true;
        }

        cubeId = default;
        ownerPlayerName = string.Empty;
        return false;
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

        if (_playerProfiles == null || _knownProfileNames.Contains(ownerPlayerName))
        {
            return true;
        }

        // Rare: an owner who neither joined nor owns a restored cube. Cache a positive answer.
        if (_playerProfiles.TryGet(ownerPlayerName) != null)
        {
            _knownProfileNames.Add(ownerPlayerName);
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

    private static Vector3 GetCellCenter(Vector3 cellPosition)
    {
        return new Vector3(
            MathF.Floor(cellPosition.X + 1e-6f) + 0.5f,
            MathF.Floor(cellPosition.Y + 1e-6f) + 0.5f,
            MathF.Floor(cellPosition.Z + 1e-6f) + 0.5f);
    }
}
