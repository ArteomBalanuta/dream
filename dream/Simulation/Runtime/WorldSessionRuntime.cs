using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Enemies;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

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
    private readonly Dictionary<PlayerSessionId, PlayerInteractionMode> _sessionInteractionModes = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<GeometryCellKey, PersistedFractureGroup> _persistedFractureGroups = [];
    private readonly IPlayerProfileRepository? _playerProfiles;
    private readonly IWorldPersistenceRepository? _worldPersistence;
    private readonly IPersistenceCheckpointWriter? _checkpointWriter;
    // Immediate writes go through an ordered queue (background on the dedicated server) and the
    // identity/profile lookups the tick needs are served from memory, so gameplay actions no
    // longer read whole tables or commit on the simulation thread.
    private readonly PersistenceWriteQueue? _writes;
    private readonly Dictionary<string, PersistedPlayerProfile> _profileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownProfileNames = new(StringComparer.OrdinalIgnoreCase);
    // Sessions whose join failed before their saved state was restored (RestorePersistedPlayerState);
    // their in-memory stats are defaults, so PersistPlayerProfile must not save them over the row.
    // Keyed by session, not display name, so another session sharing the name still saves; an
    // entry goes when the session restores or is detached.
    private readonly HashSet<PlayerSessionId> _unrestoredPlayerSessions = [];
    private readonly Dictionary<int, PersistedPlacedCube> _persistedCubes = [];
    private readonly CombatFormulaService _combatFormulaService;
    private PlayerCommandFrame _lastAppliedCommandFrame;
    // Ticks since the primary seat last accepted a command; see MaxCommandRepeatTicks.
    private int _primaryTicksSinceCommand;
    // Set while a cube registration must not export a CubeIdentified delta (see OnCubeIdAssigned).
    private bool _suppressCubeIdExports;

    public WorldSessionRuntime(
        int floorSize,
        Vector3 playerStartPosition,
        SceneEventHub events,
        IPlayerProfileRepository? playerProfiles = null,
        IWorldPersistenceRepository? worldPersistence = null,
        CombatFormulaService? combatFormulaService = null,
        IPersistenceCheckpointWriter? checkpointWriter = null,
        PersistenceWriteQueue? persistenceWrites = null,
        ulong simulationSeed = DefaultSimulationSeed)
    {
        SimulationSeed = simulationSeed;
        _writes = persistenceWrites ??
            (worldPersistence != null || playerProfiles != null ? PersistenceWriteQueue.Inline() : null);
        _events = events;
        _playerProfiles = playerProfiles;
        _worldPersistence = worldPersistence;
        _checkpointWriter = checkpointWriter ??
            (worldPersistence != null ? new RepositoryPersistenceCheckpointWriter(playerProfiles, worldPersistence) : null);
        _combatFormulaService = combatFormulaService ?? CombatFormulaService.Default;
        Physics = new PhysicsIntegrator();
        Map = CreateMap(floorSize);

        PhysicsFactory physicsFactory = new();
        var playerBody = physicsFactory.CreatePlayerBody(playerStartPosition.ToBullet());
        Physics.RegisterPlayerBody(playerBody, PhysicsBodyOwnership.IntegratorOwned);
        Player = new Player(Map, Physics, playerBody, playerStartPosition, _events, _combatFormulaService);
        LocalPlayerSessionId = PlayerSessionId.LocalDefault;
        _sessions.Register(
            LocalPlayerSessionId,
            Player.Combatant.Actor.Id,
            isLocal: true,
            displayName: "local-player");
        _sessionInteractionModes[LocalPlayerSessionId] = PlayerInteractionMode.Building;
        _lastAppliedCommandFrame = CreateIdleCommandFrame(LocalPlayerSessionId, sequence: 0);

        _worldObjects = new WorldObjectRegistry();
        _enemies = new EnemyRegistry();
        SubscribeReplicationExports();

        RestorePersistedWorldState();
        // After the restore: the world a client joins already carries the restored cubes' ids.
        _worldObjects.CubeIdAssigned = OnCubeIdAssigned;
    }

    public Map Map { get; private set; }

    public ulong LastWorldMutationSequence { get; private set; }

    private Map CreateMap(int floorSize)
    {
        return new Map(
            Physics,
            generateStartupTerrain: true,
            width: floorSize,
            depth: floorSize);
    }

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

    // Root of every seeded simulation roll; see ResolveSeededAttack.
    public ulong SimulationSeed { get; }

    public void RebuildMapForFloorSize(int floorSize)
    {
        Vector3 currentPlayerPosition = Player.GetWorldPosition();
        Map previousMap = Map;
        WorldObjectCubeSnapshot worldObjectSnapshot = _worldObjects.CreateCubeSnapshot();

        Physics.ResetMapBodies();

        var rebuiltMap = CreateMap(floorSize);
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
        FinalCheckpoint? finalCheckpoint = WriteFinalCheckpoint();
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
