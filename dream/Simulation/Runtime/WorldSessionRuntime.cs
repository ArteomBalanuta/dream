using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Enemies;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;

namespace Dream.Simulation.Runtime;


public readonly record struct WorldSessionUpdateContext(
    float DeltaTime,
    PlayerCommandFrame CommandFrame,
    PlayerSimulationTuning PlayerTuning,
    bool AllowGameplaySimulation = true);

public readonly record struct WorldSessionUpdateResult(
    bool PrimaryUseTriggered,
    AuthoritativeActionResult? ActionResult = null);

// WorldSessionRuntime is the first authoritative simulation seam extracted from PlayScene.
// It owns runtime truth and stable update order, while the desktop scene remains responsible
// for client-only input capture, viewmodel presentation, HUD, and rendering.
public sealed partial class WorldSessionRuntime : IDisposable
{
    // Seed used when the host supplies none. Every attack roll derives from this seed plus the
    // tick, session, action sequence and target, so a fixed default keeps runs reproducible.
    public const ulong DefaultSimulationSeed = 0x6472_6561_6D5F_7631UL;

    // A starved seat (no accepted command this tick) repeats its last command for at most this many
    // ticks (250 ms at 64 Hz), then simulates an idle frame until a command arrives, so a stalled
    // client stops walking and using instead of repeating its last input until it disconnects.
    public const int MaxCommandRepeatTicks = 16;

    private readonly SceneEventHub _events;
    private readonly WorldObjectRegistry _worldObjects;
    private readonly EnemyRegistry _enemies;
    private readonly PlayerSessionRegistry _sessions = new();
    private readonly Dictionary<PlayerSessionId, RemoteSessionPlayerState> _secondaryRemotePlayers = new();
    // _secondaryRemotePlayers' values in ordinal session-id order; rebuilt on attach and detach.
    private RemoteSessionPlayerState[] _orderedSecondaryRemotePlayers = [];
    private bool _disposed;
    private bool _skipFinalCheckpoint;
    // The primary seat's player target, created once per primary session id. Its delegates read
    // live state; it is dropped on attach, detach and seat changes and rebuilt on next use.
    private AuthoritativePlayerTarget? _primaryPlayerTarget;
    // Reused target lists: the tick's spawned and alive players, and every listed player for a snapshot.
    private readonly List<IAuthoritativePlayerTarget> _tickPlayerTargets = [];
    private readonly List<IAuthoritativePlayerTarget> _snapshotPlayerTargets = [];
    // Reused scratch for sorting queued actions and coalescing mutations; never handed out.
    private readonly List<(QueuedPlayerAction Action, int Index)> _orderedQueuedActions = [];
    private readonly List<WorldMutationDelta> _coalescedMutations = [];
    private readonly HashSet<int> _coalescedLaterActors = [];
    // Last experience-to-next-level answer per player actor id; see EvaluateSnapshotExperienceToNextLevel.
    private readonly Dictionary<int, (int Level, int Experience, int Required)> _experienceToNextLevelByActor = [];
    // Per-session interaction mode, kept while the registry remembers the session (a returning
    // session keeps its mode) and dropped when it forgets it (OnSessionForgotten), so the map is
    // bounded by the registry however many session ids connect.
    private readonly Dictionary<PlayerSessionId, PlayerInteractionMode> _sessionInteractionModes = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly IPlayerProfileRepository? _playerProfiles;
    // Immediate writes go through an ordered queue (background on the dedicated server) and the
    // identity/profile lookups the tick needs are served from memory, so gameplay actions no
    // longer read whole tables or commit on the simulation thread.
    private readonly PersistenceWriteQueue? _writes;
    // World restore, join prefetch, profile cache, write-through and checkpoints (ARCH-3 step B).
    private readonly RuntimePersistenceBridge _persistence;
    // Wave C: one counter for every persisted object id, and the ObjectId <-> actor id map
    // for placed cubes and enemies. Tick thread only.
    private readonly ObjectIdAllocator _objectIds = new();
    private readonly ObjectIdentityMap _objectIdentities = new();
    private readonly CombatFormulaService _combatFormulaService;
    private readonly AuthoritativeCombatResolver _combatResolver;
    private PlayerCommandFrame _lastAppliedCommandFrame;
    // Ticks since the primary seat last accepted a command; see MaxCommandRepeatTicks.
    private int _primaryTicksSinceCommand;
    // Set while a cube registration must not export a CubeIdentified delta (see OnCubeIdAssigned).
    private bool _suppressCubeIdExports;
    // Wave I: where a respawning player is put (the runtime's player start position); see
    // ResolveRespawnPosition.
    private readonly Vector3 _worldSpawn;

    // A square default-generator world of the given floor size (id 1, "default").
    public WorldSessionRuntime(
        int floorSize,
        Vector3 playerStartPosition,
        SceneEventHub events,
        IPlayerProfileRepository? playerProfiles = null,
        IWorldPersistenceRepository? worldPersistence = null,
        CombatFormulaService? combatFormulaService = null,
        IPersistenceCheckpointWriter? checkpointWriter = null,
        PersistenceWriteQueue? persistenceWrites = null,
        ulong simulationSeed = DefaultSimulationSeed,
        bool headlessMap = true)
        : this(
            WorldDescriptor.ForFloorSize(floorSize),
            playerStartPosition,
            events,
            playerProfiles,
            worldPersistence,
            combatFormulaService,
            checkpointWriter,
            persistenceWrites,
            simulationSeed,
            headlessMap)
    {
    }

    // The runtime's map is built from the descriptor (width and depth; the height is the map's
    // fixed one). The world id never enters simulation state, snapshots or checksums.
    public WorldSessionRuntime(
        WorldDescriptor world,
        Vector3 playerStartPosition,
        SceneEventHub events,
        IPlayerProfileRepository? playerProfiles = null,
        IWorldPersistenceRepository? worldPersistence = null,
        CombatFormulaService? combatFormulaService = null,
        IPersistenceCheckpointWriter? checkpointWriter = null,
        PersistenceWriteQueue? persistenceWrites = null,
        ulong simulationSeed = DefaultSimulationSeed,
        bool headlessMap = true,
        IReadOnlyList<PersistedTerrainChunk>? storedTerrain = null,
        IAccountRepository? accounts = null,
        ContentCatalog? enemyContent = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        World = world;
        // Before the restore below, which rebuilds the stored enemies from it.
        EnemyContent = enemyContent ?? DefaultEnemyContent;
        StoredTerrain = storedTerrain;
        SimulationSeed = simulationSeed;
        _worldSpawn = playerStartPosition;
        _headlessMap = headlessMap;
        _writes = persistenceWrites ??
            (worldPersistence != null || playerProfiles != null ? PersistenceWriteQueue.Inline() : null);
        _events = events;
        _playerProfiles = playerProfiles;
        _inventory = new PlayerInventoryService(worldPersistence, _writes, _objectIds)
        {
            ActionGate = SpendInventoryActionBudget,
        };
        IPersistenceCheckpointWriter? resolvedCheckpointWriter = checkpointWriter ??
            (worldPersistence != null ? new RepositoryPersistenceCheckpointWriter(playerProfiles, worldPersistence) : null);
        _combatFormulaService = combatFormulaService ?? CombatFormulaService.Default;
        Physics = new PhysicsIntegrator();
        try
        {
            Map = CreateMap(world);
        }
        catch
        {
            // The constructor throws: nobody owns the native physics world.
            Physics.Dispose();
            throw;
        }

        PhysicsFactory physicsFactory = new();
        var playerBody = physicsFactory.CreatePlayerBody(playerStartPosition.ToBullet());
        Physics.RegisterPlayerBody(playerBody, PhysicsBodyOwnership.IntegratorOwned);
        Player = new Player(Map, Physics, playerBody, playerStartPosition, _events, _combatFormulaService);
        LocalPlayerSessionId = PlayerSessionId.LocalDefault;
        _sessions.SessionForgotten = OnSessionForgotten;
        ConnectedPlayer localSeat = _sessions.Register(
            LocalPlayerSessionId,
            Player.Combatant.Actor.Id,
            isLocal: true,
            displayName: "local-player");
        // The first spawn is protected like a respawn (see AttachRemoteSession).
        localSeat.ProtectedThroughTick = PlayerRespawnRules.SpawnProtectionTicks;
        _sessionInteractionModes[LocalPlayerSessionId] = PlayerInteractionMode.Building;
        _lastAppliedCommandFrame = CreateIdleCommandFrame(LocalPlayerSessionId, sequence: 0);

        _worldObjects = new WorldObjectRegistry();
        _enemies = new EnemyRegistry();
        _combatResolver = CreateCombatResolver();
        _persistence = new RuntimePersistenceBridge(
            this, _playerProfiles, worldPersistence, resolvedCheckpointWriter, _writes, _worldObjects, _enemies, _events, _objectIds, _objectIdentities, accounts);
        SubscribeReplicationExports();

        try
        {
            RestorePersistedWorldState();
        }
        catch
        {
            // The constructor throws: nobody owns the player or the native physics world.
            Player.Dispose();
            Physics.Dispose();
            throw;
        }

        // After the restore: the world a client joins already carries the restored cubes' ids.
        _worldObjects.CubeIdAssigned = OnCubeIdAssigned;
    }

    // Disposes a runtime whose host failed to construct: the world was never served, so no final
    // checkpoint is written over what the database holds.
    internal void DisposeWithoutFinalCheckpoint()
    {
        _skipFinalCheckpoint = true;
        Dispose();
    }

    internal ObjectIdAllocator ObjectIds => _objectIds;

    internal ObjectIdentityMap ObjectIdentities => _objectIdentities;

    public Map Map { get; private set; }

    // The world this runtime hosts: the descriptor its map was built from. Replaced (same id and
    // name, new size) by RebuildMapForFloorSize.
    public WorldDescriptor World { get; private set; }

    public ulong LastWorldMutationSequence { get; private set; }

    // The runtime is the authoritative server's simulation and never renders (clients render their
    // own ClientWorldSession map), so its map is headless by default: it skips render block
    // descriptors, merged render blocks and render-dirty tracking. headlessMap: false keeps them,
    // for tests that inspect the server map's render export.
    private readonly bool _headlessMap;

    private Map CreateMap(WorldDescriptor world)
    {
        try
        {
            return new Map(MapSources.ForWorld(world, SimulationSeed, StoredTerrain), Physics, headless: _headlessMap);
        }
        catch (InvalidDataException ex) when (world.MapSource == WorldMapSource.Stored)
        {
            // The stored blobs decode lazily, here; name the failure for the bootstrap.
            throw new StoredTerrainInvalidException(ex.Message, ex);
        }
    }

    // The terrain chunks a stored world was built from; null for generated worlds.
    public IReadOnlyList<PersistedTerrainChunk>? StoredTerrain { get; }

    public PhysicsIntegrator Physics { get; }

    public Player Player { get; }

    public EnemyRegistry Enemies => _enemies;

    public PlayerInteractionMode InteractionMode => GetSessionInteractionMode(ResolvePrimaryControlledSessionId());

    public void SetAuthoritativeInteractionMode(PlayerInteractionMode interactionMode)
    {
        SetSessionInteractionMode(ResolvePrimaryControlledSessionId(), interactionMode);
    }

    public PlayerSessionId LocalPlayerSessionId { get; }

    public PlayerSessionId? PrimaryRemoteSessionId { get; private set; }

    public PlayerSessionRegistry Sessions => _sessions;

    public ulong CurrentServerTick { get; private set; }

    // M4: whether players can damage each other. Off by default; the dedicated bootstrap sets it from
    // DREAM_PVP. Read per use by the combat host. Cubes stay owner-only whatever this says.
    public bool PvpEnabled { get; set; }

    // Root of every seeded simulation roll; see AuthoritativeCombatResolver.ResolveSeededAttack.
    public ulong SimulationSeed { get; }

    public void RebuildMapForFloorSize(int floorSize)
    {
        if (World.MapSource == WorldMapSource.Stored)
        {
            throw new NotSupportedException("a stored world cannot be resized");
        }

        Vector3 currentPlayerPosition = Player.GetWorldPosition();
        Map previousMap = Map;
        WorldObjectCubeSnapshot worldObjectSnapshot = _worldObjects.CreateCubeSnapshot();

        Physics.ResetMapBodies();

        WorldDescriptor rebuiltWorld = World with { Width = floorSize, Depth = floorSize };
        var rebuiltMap = CreateMap(rebuiltWorld);
        previousMap.CopyPlacedCubesTo(rebuiltMap);
        // Re-keys the same ids on the rebuilt map; nothing new for clients to learn.
        _suppressCubeIdExports = true;
        try
        {
            _worldObjects.RestoreCubeSnapshot(rebuiltMap, worldObjectSnapshot);
        }
        finally
        {
            _suppressCubeIdExports = false;
        }

        Map = rebuiltMap;
        World = rebuiltWorld;
        Player.BindMap(rebuiltMap);
        foreach (RemoteSessionPlayerState secondary in _orderedSecondaryRemotePlayers)
        {
            secondary.PhysicsPlayer.BindMap(rebuiltMap);
        }
        _enemies.ClampToMapBounds(rebuiltMap);

        Vector3 clampedPosition = new(
            Math.Clamp(currentPlayerPosition.X, 0.5f, rebuiltMap.Width - 0.5f),
            Math.Clamp(currentPlayerPosition.Y, 1.5f, rebuiltMap.Height - 0.5f),
            Math.Clamp(currentPlayerPosition.Z, 0.5f, rebuiltMap.Depth - 0.5f));

        Player.SetWorldPosition(clampedPosition);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        // Shutdown flush: a full checkpoint behind the queued immediate writes, then finish those
        // writes before the owner disposes the repositories.
        PendingFinalCheckpoint? finalCheckpoint = _skipFinalCheckpoint ? null : WriteFinalCheckpoint();
        _writes?.Dispose();
        finalCheckpoint?.WriteIfDropped(_writes);

        // Every player's contact callback tests against the physics world, so players go before it.
        // Secondary states remove their own session bodies; Physics.Dispose then finds none left.
        foreach (RemoteSessionPlayerState secondary in _secondaryRemotePlayers.Values)
        {
            secondary.Dispose();
        }

        _secondaryRemotePlayers.Clear();
        _orderedSecondaryRemotePlayers = [];
        Player.Dispose();
        Physics.Dispose();
    }
}
