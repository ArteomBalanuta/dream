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

    public void RestorePersistedPlayerState(PlayerSessionId sessionId, string playerName)
    {
        if (!_persistence.TryLoadPlayerForRestore(
            sessionId,
            playerName,
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
        playerTarget.Combatant.ApplyProgression(profile.Stats.Level, profile.Stats.Experience);

        Vector3 persistedPosition = worldState?.Position ?? playerTarget.Position;
        Vector3 persistedFacing = worldState?.Facing ?? playerTarget.Facing;
        // The world-state row is the newest written HP: checkpoints write it with the profile's
        // vitals, and a leave writes both (DetachRemoteSession), so the two agree after a leave.
        // The profile's HP is only the fallback for a player without a world-state row.
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

    bool IRuntimePersistenceHost.TryAllocateEnemyActorId(out int actorId) => TryAllocateEnemyActorId(out actorId);

    void IRuntimePersistenceHost.SpawnInitialWolf() => SpawnInitialWolf();

    bool IRuntimePersistenceHost.IsPlayerInventoryLoaded(string playerName) =>
        _inventory.IsLoaded(playerName);

    void IRuntimePersistenceHost.EvictPlayerInventory(string playerName) => EvictPlayerInventory(playerName);
}
