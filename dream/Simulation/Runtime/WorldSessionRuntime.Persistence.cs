using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Runtime;

// Persistence: the runtime side of RuntimePersistenceBridge (ARCH-3 step B). The bridge owns the
// world restore, the join prefetch, the profile cache and the cube, fracture, enemy and profile
// write-through; this partial keeps the runtime's public persistence API and the private helpers
// the other partials call by name, all delegating to it, and applies a restored player's state.
// The runtime implements IRuntimePersistenceHost for the bridge's callbacks.
public sealed partial class WorldSessionRuntime : IRuntimePersistenceHost
{
    // True when the runtime was built with an account repository (joins are checked against accounts).
    public bool HasAccounts => _persistence.HasAccounts;

    public PersistedPlayerPrefetch? BeginPersistedPlayerPrefetch(string playerName, AccountKey? account = null) =>
        _persistence.BeginPersistedPlayerPrefetch(playerName, account);

    public PersistedPlayerLoadState GetPersistedPlayerLoadState(string playerName, AccountKey? account = null) =>
        _persistence.GetPersistedPlayerLoadState(playerName, account);

    public bool IsPersistedPlayerReady(string playerName, bool startIfMissing, AccountKey? account = null) =>
        _persistence.IsPersistedPlayerReady(playerName, startIfMissing, account);

    public void DiscardPersistedPlayerPrefetch(string playerName, AccountKey? account = null) =>
        _persistence.DiscardPersistedPlayerPrefetch(playerName, account);

    // What the join's prefetch read (or, with none, a synchronous read after the queued writes) about the
    // character the typed name reaches; see RuntimePersistenceBridge.ResolveCharacterLookup.
    public CharacterLookup ResolveCharacterLookup(string typedName, AccountKey? account) =>
        _persistence.ResolveCharacterLookup(typedName, account);

    // Queues the account row and ownership write of a checked join that was applied (G-D2).
    public void CommitCharacterAccess(string typedName, in CharacterAccessDecision access) =>
        _persistence.CommitCharacterAccess(typedName, access);

    public void RestorePersistedPlayerState(PlayerSessionId sessionId, string playerName) =>
        RestorePersistedPlayerState(sessionId, playerName, playerName, account: null);

    // playerName is the character key; prefetchName and account find the join's prefetch.
    public void RestorePersistedPlayerState(
        PlayerSessionId sessionId,
        string playerName,
        string prefetchName,
        AccountKey? account)
    {
        if (!_persistence.TryLoadPlayerForRestore(
            sessionId,
            playerName,
            prefetchName,
            account,
            out PersistedPlayerProfile profile,
            out PersistedPlayerWorldState? worldState,
            out PersistedPlayerInventory? inventory))
        {
            return;
        }

        ApplyPersistedPlayerState(sessionId, profile, worldState);
        ApplyLoadedInventory(profile.PlayerName, inventory);
        EnsurePlayerInventory(profile.PlayerName);
        RefreshAuthoritativePlayerCombatStats(sessionId);
        ReapplyPersistedHealth(sessionId, worldState);
        // Experience saved under an older, steeper curve may now be a full level or more: the join
        // levelled it (ApplyPersistedPlayerState), so the profile is saved with the result.
        if (TryResolveAuthoritativePlayerTarget(sessionId, out _, out IAuthoritativePlayerTarget? restored)
            && restored != null
            && (restored.Combatant.Level != profile.Stats.Level || restored.Combatant.Experience != profile.Stats.Experience))
        {
            PersistPlayerProfile(sessionId, restored.Combatant);
        }
    }

    public PersistenceWriteQueue? PersistenceWrites => _writes;

    // The world repository the runtime was given (tests save rows through it directly).
    internal IWorldPersistenceRepository? WorldPersistence => _persistence.WorldPersistence;

    // Sessions whose failed join left default stats that must not be saved (tests).
    internal int UnrestoredPlayerSessionCount => _persistence.UnrestoredPlayerSessionCount;

    public void ApplyPersistedPlayerState(
        PlayerSessionId sessionId,
        PersistedPlayerProfile profile,
        PersistedPlayerWorldState? worldState)
    {
        // Saved even when unchanged, as before: the join's profile save.
        SetSessionInteractionMode(sessionId, profile.InteractionMode, saveUnchanged: true);
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
        // Run the level-up loop once over the stored values (the same loop a kill uses): experience that
        // outgrew the formula it was saved under becomes levels instead of an over-100 percent bar. The
        // bonus levels are accepted.
        int level = Math.Max(1, profile.Stats.Level);
        int experience = Math.Max(0, profile.Stats.Experience);
        int required = _combatFormulaService.EvaluateExperienceToNextLevel(level, experience);
        while (experience >= required)
        {
            experience -= required;
            level++;
            required = _combatFormulaService.EvaluateExperienceToNextLevel(level, experience);
        }

        playerTarget.Combatant.ApplyProgression(level, experience);

        // A primary seat with no row starts at the world spawn, not where the last occupant stood.
        Vector3 fallbackPosition = worldState == null && sessionId == ResolvePrimaryControlledSessionId()
            ? ResolveRespawnPosition(Map, _worldSpawn, _respawnRubbleScratch)
            : playerTarget.Position;
        Vector3 persistedPosition = worldState?.Position ?? fallbackPosition;
        Vector3 persistedFacing = worldState?.Facing ?? playerTarget.Facing;
        playerTarget.SetWorldPosition(persistedPosition);
        if (sessionId == ResolvePrimaryControlledSessionId())
        {
            Player.SetFacing(persistedFacing);
        }
        else if (_secondaryRemotePlayers.TryGetValue(sessionId, out RemoteSessionPlayerState? remote))
        {
            remote.SetFacing(persistedFacing);
        }

        ApplyPersistedHealth(sessionId, playerTarget, worldState);
    }

    // The join set health against the base max, before equipment; the equipment refresh then rescaled
    // it by ratio, so a vest would heal or hurt on every rejoin (H-D17). Sets it again against the
    // final max.
    private void ReapplyPersistedHealth(PlayerSessionId sessionId, PersistedPlayerWorldState? worldState)
    {
        if (TryResolveAuthoritativePlayerTarget(sessionId, out _, out IAuthoritativePlayerTarget? playerTarget)
            && playerTarget != null)
        {
            ApplyPersistedHealth(sessionId, playerTarget, worldState);
        }
    }

    private void ApplyPersistedHealth(
        PlayerSessionId sessionId,
        IAuthoritativePlayerTarget playerTarget,
        PersistedPlayerWorldState? worldState)
    {
        // Health belongs to a world (U2): the world-state row holds it. The profile is global, so its
        // HP is whatever the character had in the last world it left; a character with no row here
        // has not been to this world and arrives at full health.
        int maxHealth = playerTarget.Combatant.DerivedStats.MaxHealth;
        int currentHealth = worldState?.CurrentHealth ?? maxHealth;
        if (sessionId == ResolvePrimaryControlledSessionId())
        {
            Player.SetHealth(currentHealth, maxHealth);
        }
        else if (_secondaryRemotePlayers.TryGetValue(sessionId, out RemoteSessionPlayerState? remote))
        {
            remote.SetHealth(currentHealth, maxHealth);
        }

        // Mana is per world like health (player_world_state.current_energy). SetMana clamps, which is
        // what lets migration 0013's backfill value leave a returning character at full mana.
        playerTarget.Combatant.SetMana(worldState?.CurrentEnergy ?? playerTarget.Combatant.MaxMana);
    }

    private void RestorePersistedWorldState() => _persistence.RestoreWorld();

    private void PersistPlayerProfile(PlayerSessionId sessionId, PlayerCombatant combatant) =>
        _persistence.PersistPlayerProfile(sessionId, combatant);

    private void PersistPlacedCube(WorldObjectRecord record, GeometryMaterialId materialId) =>
        _persistence.PersistPlacedCube(record, materialId);

    private void UpdatePersistedCubeHealth(WorldObjectRecord record) =>
        _persistence.UpdatePersistedCubeHealth(record);

    private void DeletePersistedPlacedCube(int cubeId) => _persistence.DeletePersistedPlacedCube(cubeId);

    private void EnqueueWorldWrite(Action<IWorldPersistenceRepository> write) => _persistence.EnqueueWorldWrite(write);

    private PersistedPlayerProfile GetCachedProfile(string playerName) => _persistence.GetCachedProfile(playerName);

    private void SaveProfile(string playerName, PersistedPlayerProfile profile) =>
        _persistence.SaveProfile(playerName, profile);

    internal const int DefaultMaxDepartedProfiles = RuntimePersistenceBridge.DefaultMaxDepartedProfiles;

    // Settable for tests.
    internal int MaxDepartedProfiles
    {
        get => _persistence.MaxDepartedProfiles;
        set => _persistence.MaxDepartedProfiles = value;
    }

    internal int ProfileCacheCount => _persistence.ProfileCacheCount;

    internal int DepartedProfileCount => _persistence.DepartedProfileCount;

    internal int KnownProfileNameCount => _persistence.KnownProfileNameCount;

    private void MarkProfileActive(string playerName) => _persistence.MarkProfileActive(playerName);

    private void MarkProfileDeparted(string playerName) => _persistence.MarkProfileDeparted(playerName);

    private void PersistFracturePieces(WorldObjectRecord record, GeometryCellKey cell) =>
        _persistence.PersistFracturePieces(record, cell);

    private void PersistTerrainCellRemoved(WorldObjectRecord record, GeometryCellKey cell) =>
        _persistence.PersistTerrainCellRemoved(record, cell);

    private void PersistEnemy(EnemyActor enemy) => _persistence.PersistEnemy(enemy);

    private void DeletePersistedEnemy(int enemyId) => _persistence.DeletePersistedEnemy(enemyId);

    private WorldObjectRecord RepairCubeRecordFromPersistence(WorldObjectRecord record) =>
        _persistence.RepairCubeRecordFromPersistence(record);

    // IRuntimePersistenceHost: the narrow runtime surface the bridge calls back into. Map and
    // Sessions are the public properties.
    bool IRuntimePersistenceHost.IsDisposed => Volatile.Read(ref _disposed);

    PlayerInteractionMode IRuntimePersistenceHost.GetSessionInteractionMode(PlayerSessionId sessionId) =>
        GetSessionInteractionMode(sessionId);

    bool IRuntimePersistenceHost.TryGetPlayerCheckpointState(
        PlayerSessionId sessionId,
        out Vector3 position,
        out Vector3 facing,
        out int currentHealth,
        out int maxHealth)
    {
        if (!TryResolveAuthoritativePlayerTarget(sessionId, out _, out IAuthoritativePlayerTarget? target) ||
            target == null)
        {
            position = default;
            facing = default;
            currentHealth = 0;
            maxHealth = 0;
            return false;
        }

        position = target.Position;
        facing = target.Facing;
        currentHealth = target.Combatant.Health.CurrentHealth;
        maxHealth = target.Combatant.Health.MaxHealth;
        return true;
    }

    int IRuntimePersistenceHost.GetSessionMana(PlayerSessionId sessionId) =>
        TryResolveAuthoritativePlayerTarget(sessionId, out _, out IAuthoritativePlayerTarget? target) && target != null
            ? target.Combatant.CurrentMana
            : 0;

    Vector3 IRuntimePersistenceHost.WorldSpawn => _worldSpawn;

    bool IRuntimePersistenceHost.TryAllocateEnemyActorId(out int actorId) => TryAllocateEnemyActorId(out actorId);

    // A restore with no enemy rows: the first wolf, unless the content has spawn camps (the camp step
    // fills them on the first tick).
    void IRuntimePersistenceHost.SpawnInitialWolf()
    {
        if (EnemyContent.Camps.Count == 0)
        {
            SpawnInitialWolf();
        }
    }

    bool IRuntimePersistenceHost.IsPlayerInventoryLoaded(string playerName) =>
        _inventory.IsLoaded(playerName);

    void IRuntimePersistenceHost.EvictPlayerInventory(string playerName) => EvictPlayerInventory(playerName);
}
