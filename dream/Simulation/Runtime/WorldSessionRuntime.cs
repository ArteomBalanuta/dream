using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Server.Rules;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
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
public sealed class WorldSessionRuntime : IDisposable
{
    private const float MaxAuthoritativeBuildDistance = 3f;

    // Seed used when the host supplies none. Every attack roll derives from this seed plus the
    // tick, session, action sequence and target, so a fixed default keeps runs reproducible.
    public const ulong DefaultSimulationSeed = 0x6472_6561_6D5F_7631UL;

    // Ordinal by session id, so per-tick iteration over secondary players never depends on join history.
    private static readonly Comparison<RemoteSessionPlayerState> CompareSecondaryBySessionId =
        static (left, right) => string.CompareOrdinal(left.SessionId.Value, right.SessionId.Value);

    // Receive order, then ordinal session id, then action sequence: the order of the stable LINQ
    // OrderBy/ThenBy chain this replaces. The position in the caller's list breaks full ties the
    // way that stable sort did.
    private static readonly Comparison<(QueuedPlayerAction Action, int Index)> CompareQueuedActions =
        static (left, right) =>
        {
            int order = left.Action.ReceiveOrder.CompareTo(right.Action.ReceiveOrder);
            if (order != 0)
            {
                return order;
            }

            order = string.CompareOrdinal(left.Action.SessionId.Value, right.Action.SessionId.Value);
            if (order != 0)
            {
                return order;
            }

            order = left.Action.Message.ActionSequence.CompareTo(right.Action.Message.ActionSequence);
            return order != 0 ? order : left.Index.CompareTo(right.Index);
        };

    private readonly bool _loadVisualAssets;
    private readonly SceneEventHub _events;
    private readonly PhysicsSyncSystem _physicsSync = new();
    private readonly WorldObjectRegistry _worldObjects;
    private readonly EnemyRegistry _enemies;
    private readonly PlayerSessionRegistry _sessions = new();
    private readonly Dictionary<PlayerSessionId, RemoteSessionPlayerState> _secondaryRemotePlayers = new();
    // _secondaryRemotePlayers' values in ordinal session-id order; rebuilt on attach and detach.
    private RemoteSessionPlayerState[] _orderedSecondaryRemotePlayers = [];
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
    private readonly List<WorldMutationDelta> _pendingWorldMutations = [];
    private readonly List<DamageEventDelta> _pendingDamageEvents = [];
    private readonly Dictionary<long, PersistedItemInstance> _itemInstances = [];
    private readonly Dictionary<string, Dictionary<int, long>> _inventorySlotsByPlayer = new(StringComparer.OrdinalIgnoreCase);
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
    private readonly Dictionary<int, PersistedPlacedCube> _persistedCubes = [];
    private readonly CombatFormulaService _combatFormulaService;
    private PlayerCommandFrame _lastAppliedCommandFrame;
    // A primary-seat primary use that triggered this tick but was left for the caller to resolve
    // in its ordinal pass with every other seat (see AdvanceAuthoritativeTick's deferPrimarySeatUse).
    private bool _hasDeferredPrimarySeatUse;
    private PlayerCommandFrame _deferredPrimarySeatUseFrame;
    private int _nextRemoteActorId = 2;
    private long _nextItemInstanceId = 1;

    public WorldSessionRuntime(
        int floorSize,
        Vector3 playerStartPosition,
        SceneEventHub events,
        bool loadVisualAssets = true,
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
        _loadVisualAssets = loadVisualAssets;
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
    }

    public Map Map { get; private set; }

    public ulong LastWorldMutationSequence { get; private set; }

    private Map CreateMap(int floorSize)
    {
        return new Map(
            Physics,
            loadVisualAssets: _loadVisualAssets,
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

    public ConnectedPlayer AttachRemoteSession(PlayerSessionId sessionId, string? displayName = null)
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
        player.MarkDespawned();
        player.ResetAcceptedSequences();
        _primaryPlayerTarget = null;

        if (PrimaryRemoteSessionId == sessionId)
        {
            PrimaryRemoteSessionId = null;
            _lastAppliedCommandFrame = CreateIdleCommandFrame(LocalPlayerSessionId, sequence: 0);
            // Without an occupant the primary body would stay as an invisible collider.
            Physics.SetPlayerBodyParked(true);
        }
        else if (_secondaryRemotePlayers.Remove(sessionId, out RemoteSessionPlayerState? secondary))
        {
            RebuildOrderedSecondaryRemotePlayers();
            _experienceToNextLevelByActor.Remove(secondary.ActorId);
            secondary.Dispose();
        }
    }

    public void RestorePersistedPlayerState(PlayerSessionId sessionId, string playerName)
    {
        if (_playerProfiles == null)
        {
            return;
        }

        // Join is rare and runs off the tick; make queued writes for this player visible first.
        _writes?.Flush(TimeSpan.FromSeconds(5));
        PersistedPlayerProfile profile = _playerProfiles.GetOrCreate(playerName);
        CacheProfile(playerName, profile);
        PersistedPlayerWorldState? worldState = _worldPersistence?.TryGetPlayerWorldState(playerName);
        ApplyPersistedPlayerState(sessionId, profile, worldState);
        EnsurePlayerInventory(profile.PlayerName);
        RefreshAuthoritativePlayerCombatStats(sessionId);
    }

    public PlayerInventorySnapshot? TryCaptureInventorySnapshot(PlayerSessionId sessionId)
    {
        if (!_sessions.TryGet(sessionId, out ConnectedPlayer? player) || player == null)
        {
            return null;
        }

        EnsurePlayerInventory(player.DisplayName);
        RefreshAuthoritativePlayerCombatStats(sessionId);
        return BuildInventorySnapshot(player.DisplayName);
    }

    public WorldSessionUpdateResult Update(WorldSessionUpdateContext context)
    {
        if (!_sessions.TryAcceptCommand(context.CommandFrame.SessionId, context.CommandFrame.Sequence, out _))
        {
            return new WorldSessionUpdateResult(false);
        }

        _lastAppliedCommandFrame = context.CommandFrame;
        return UpdateAcceptedCommand(
            context.DeltaTime,
            context.CommandFrame,
            context.PlayerTuning,
            context.AllowGameplaySimulation,
            deferPrimarySeatUse: false);
    }

    // The primary seat's command frame whose primary use triggered in the last tick advanced with
    // deferPrimarySeatUse, once. The caller resolves it with TryCreateCommandPrimaryUseActionMessage
    // and ProcessAuthoritativeAction in the same ordinal session-id pass as the secondary seats.
    public bool TryTakeDeferredPrimarySeatUse(out PlayerCommandFrame commandFrame)
    {
        commandFrame = _deferredPrimarySeatUseFrame;
        bool taken = _hasDeferredPrimarySeatUse;
        _hasDeferredPrimarySeatUse = false;
        return taken;
    }

    private WorldSessionUpdateResult UpdateAcceptedCommand(
        float delta,
        PlayerCommandFrame commandFrame,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation,
        bool deferPrimarySeatUse)
    {
        _hasDeferredPrimarySeatUse = false;
        Player.Combatant.AdvanceCombat(delta);
        PlayerInteractionCommand interaction = commandFrame.ToInteractionCommand();
        ApplyInteractionModeSelection(commandFrame.SessionId, interaction);
        ApplyCommandFacing(commandFrame);
        PlayerSimulationInput playerInput = CreatePlayerSimulationInput(commandFrame);
        AuthoritativeActionResult? actionResult = null;

        bool primaryUseTriggered = Player.ConsumePrimaryUsePressed(
            interaction,
            Player.Combatant.DerivedStats.AttackIntervalSeconds,
            delta);

        if (primaryUseTriggered && deferPrimarySeatUse)
        {
            // Resolved by the caller after the tick, ordered with the secondary seats, so holding
            // the primary seat (joining first) no longer wins every same-tick race.
            _deferredPrimarySeatUseFrame = commandFrame;
            _hasDeferredPrimarySeatUse = true;
        }
        else if (primaryUseTriggered)
        {
            PlayerActionMessage? actionMessage = TryCreateCommandPrimaryUseActionMessage(commandFrame);
            if (actionMessage != null)
            {
                actionResult = ProcessAuthoritativeAction(actionMessage);
            }
        }

        if (!Physics.IsPlayerBodyParked)
        {
            Player.Update(
                delta,
                playerInput,
                playerTuning);
        }

        foreach (RemoteSessionPlayerState secondary in _orderedSecondaryRemotePlayers)
        {
            secondary.AdvanceCombat(delta);
            secondary.Simulate(delta, playerTuning);
        }

        Physics.StepSimulationFixed(delta);
        if (!Physics.IsPlayerBodyParked)
        {
            Player.SyncCameraFromBody(playerTuning);
        }

        foreach (RemoteSessionPlayerState secondary in _orderedSecondaryRemotePlayers)
        {
            secondary.SyncAfterPhysicsStep(playerTuning);
        }

        if (allowGameplaySimulation)
        {
            // Filled once, before the enemies act, so a player killed this tick is still separated.
            FillAuthoritativePlayerTargets(_tickPlayerTargets, spawnedAndAliveOnly: true);
            _enemies.UpdateAll(delta, _tickPlayerTargets, _events, Map);
            _enemies.PruneInactive();
            EnsureWolfExists();
            ResolvePlayerEnemySeparation(_tickPlayerTargets);
            // Do not keep a detached (disposed) secondary referenced until the next tick.
            _tickPlayerTargets.Clear();
        }

        PhysicsSyncResult syncResult = _physicsSync.SyncDynamicMapBodies(Map, Physics.DynamicMapCubeBodies);
        IReadOnlyList<PhysicsCubeMoveDelta> movedCubes = syncResult.MovedCubes;
        for (int i = 0; i < movedCubes.Count; i++)
        {
            PhysicsCubeMoveDelta movedCube = movedCubes[i];
            QueueWorldMutation(new WorldMutationDelta(
                WorldMutationKind.CubeMoved,
                movedCube.SourceCellPosition,
                movedCube.TargetCellPosition,
                movedCube.CenterPosition,
                ActorId: _worldObjects.TryGet(movedCube.Cube)?.Actor.Id));
        }

        return new WorldSessionUpdateResult(primaryUseTriggered, actionResult);
    }

    public WorldSessionUpdateResult AdvanceAuthoritativeTick(
        ulong serverTick,
        float deltaTime,
        PlayerCommandFrame commandFrame,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true,
        bool deferPrimarySeatUse = false)
    {
        return AdvanceAuthoritativeTick(
            serverTick,
            deltaTime,
            (PlayerCommandFrame?)commandFrame,
            playerTuning,
            allowGameplaySimulation,
            deferPrimarySeatUse);
    }

    public IReadOnlyList<AuthoritativeActionResult> ProcessQueuedAuthoritativeActions(
        ulong serverTick,
        IReadOnlyList<QueuedPlayerAction> queuedActions)
    {
        CurrentServerTick = serverTick;
        if (queuedActions.Count == 0)
        {
            return Array.Empty<AuthoritativeActionResult>();
        }

        _orderedQueuedActions.Clear();
        for (int i = 0; i < queuedActions.Count; i++)
        {
            _orderedQueuedActions.Add((queuedActions[i], i));
        }

        _orderedQueuedActions.Sort(CompareQueuedActions);

        // The results go back to the caller and on to the clients, so they get a fresh array.
        AuthoritativeActionResult[] results = new AuthoritativeActionResult[_orderedQueuedActions.Count];
        for (int i = 0; i < results.Length; i++)
        {
            results[i] = ProcessAuthoritativeAction(_orderedQueuedActions[i].Action.Message);
        }

        _orderedQueuedActions.Clear();
        return results;
    }

    public WorldSessionUpdateResult AdvanceAuthoritativeTick(
        ulong serverTick,
        float deltaTime,
        PlayerCommandFrame? commandFrame,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true,
        bool deferPrimarySeatUse = false)
    {
        CurrentServerTick = serverTick;

        if (commandFrame.HasValue
            && _sessions.TryAcceptCommand(commandFrame.Value.SessionId, commandFrame.Value.Sequence, out _))
        {
            _lastAppliedCommandFrame = commandFrame.Value;
        }

        return UpdateAcceptedCommand(
            deltaTime,
            _lastAppliedCommandFrame,
            playerTuning,
            allowGameplaySimulation,
            deferPrimarySeatUse);
    }

    public bool TryResolveCombatActor(int actorId, out CombatActorHandle actor)
    {
        // Scans the listed players directly rather than filling a shared target list, so a call
        // from an event raised mid-tick cannot disturb the tick's list.
        if (TryFindListedPlayerTarget(actorId, out IAuthoritativePlayerTarget? playerTarget))
        {
            actor = playerTarget!.Combatant.Actor;
            return true;
        }

        if (_enemies.TryGetActorById(actorId, out actor))
        {
            return true;
        }

        if (_worldObjects.TryGetByActorId(actorId, out WorldObjectRecord record))
        {
            actor = record.Actor;
            return true;
        }

        actor = default;
        return false;
    }

    public void RebuildMapForFloorSize(int floorSize)
    {
        Vector3 currentPlayerPosition = Player.GetWorldPosition();
        Map previousMap = Map;
        WorldObjectCubeSnapshot worldObjectSnapshot = _worldObjects.CreateCubeSnapshot();

        Physics.ResetMapBodies();

        var rebuiltMap = CreateMap(floorSize);
        previousMap.CopyPlacedCubesTo(rebuiltMap);
        _worldObjects.RestoreCubeSnapshot(rebuiltMap, worldObjectSnapshot);

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
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        // Finish queued persistence writes before the owner disposes the repositories.
        _writes?.Dispose();
        Physics.Dispose();
    }

    public PersistenceWriteQueue? PersistenceWrites => _writes;

    public const ulong WorldChecksumIntervalTicks = 64;
    private ulong _nextWorldChecksumTick;

    public ServerSnapshotEnvelope ExportServerSnapshotEnvelope(bool includeWorldSnapshot = false)
    {
        IReadOnlyList<ActorSnapshot> actors = CaptureActorSnapshots();
        WorldSnapshot? worldSnapshot = includeWorldSnapshot
            ? ExportWorldSnapshot()
            : null;
        WorldMutationDelta[] mutations = CoalesceSameCellMoves(
            _pendingWorldMutations,
            _coalescedMutations,
            _coalescedLaterActors);

        ulong? checksum = null;
        if (CurrentServerTick >= _nextWorldChecksumTick)
        {
            checksum = WorldStateChecksum.Compute(Map);
            _nextWorldChecksumTick = CurrentServerTick + WorldChecksumIntervalTicks;
        }

        ServerSnapshotEnvelope envelope = new(
            CurrentServerTick,
            CaptureAcceptedCommandSequences(),
            actors,
            GetSessionInteractionMode(ResolvePrimaryControlledSessionId()),
            worldSnapshot,
            mutations,
            // The envelope's collections are wire payloads and stay fresh per snapshot.
            _pendingDamageEvents.Count == 0 ? Array.Empty<DamageEventDelta>() : _pendingDamageEvents.ToArray(),
            checksum,
            checksum.HasValue ? LastWorldMutationSequence : 0);

        _pendingWorldMutations.Clear();
        _pendingDamageEvents.Clear();
        return envelope;
    }

    // Full world for a client that reported a checksum mismatch. Pending deltas stay queued for
    // everyone; the world carries the sequence they are already reflected up to.
    public ServerSnapshotEnvelope ExportWorldResyncEnvelope()
    {
        return new ServerSnapshotEnvelope(
            CurrentServerTick,
            CaptureAcceptedCommandSequences(),
            CaptureActorSnapshots(),
            GetSessionInteractionMode(ResolvePrimaryControlledSessionId()),
            ExportWorldSnapshot(),
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }

    private Dictionary<string, ulong> CaptureAcceptedCommandSequences()
    {
        // Part of the envelope, so it stays a fresh dictionary, sized exactly so it never regrows.
        int acceptedCount = 0;
        foreach (ConnectedPlayer player in _sessions.Players)
        {
            if (player.LastAcceptedCommandSequence.HasValue)
            {
                acceptedCount++;
            }
        }

        Dictionary<string, ulong> acceptedSequences = new(acceptedCount);
        foreach (ConnectedPlayer player in _sessions.Players)
        {
            if (player.LastAcceptedCommandSequence.HasValue)
            {
                acceptedSequences[player.SessionId.Value] = player.LastAcceptedCommandSequence.Value;
            }
        }

        return acceptedSequences;
    }

    // A resting or jostled cube emits a same-cell CubeMoved every tick. Such a move only changes
    // the exact center, so it can be dropped whenever a later mutation for the same cube follows
    // in the batch. Cross-cell moves are kept in order because they change cell occupancy.
    public static WorldMutationDelta[] CoalesceSameCellMoves(IReadOnlyList<WorldMutationDelta> mutations)
    {
        return CoalesceSameCellMoves(mutations, new List<WorldMutationDelta>(mutations.Count), []);
    }

    // The scratch collections are cleared and reused; only the returned array (the wire payload)
    // is new, and an empty batch returns Array.Empty.
    private static WorldMutationDelta[] CoalesceSameCellMoves(
        IReadOnlyList<WorldMutationDelta> mutations,
        List<WorldMutationDelta> kept,
        HashSet<int> laterActors)
    {
        if (mutations.Count == 0)
        {
            return Array.Empty<WorldMutationDelta>();
        }

        kept.Clear();
        laterActors.Clear();
        for (int index = mutations.Count - 1; index >= 0; index--)
        {
            WorldMutationDelta mutation = mutations[index];
            if (mutation.ActorId is int actorId)
            {
                bool sameCellMove = mutation.Kind == WorldMutationKind.CubeMoved &&
                    mutation.TargetCellPosition == mutation.CellPosition;
                if (sameCellMove && laterActors.Contains(actorId))
                {
                    continue;
                }

                laterActors.Add(actorId);
            }

            kept.Add(mutation);
        }

        // kept is newest first; the batch keeps the original order.
        WorldMutationDelta[] coalesced = new WorldMutationDelta[kept.Count];
        for (int i = 0; i < coalesced.Length; i++)
        {
            coalesced[i] = kept[kept.Count - 1 - i];
        }

        kept.Clear();
        laterActors.Clear();
        return coalesced;
    }

    public IReadOnlyList<ActorSnapshot> CaptureActorSnapshots()
    {
        FillAuthoritativePlayerTargets(_snapshotPlayerTargets, spawnedAndAliveOnly: false);
        IReadOnlyList<EnemyActor> enemies = _enemies.ActiveActors;
        // The list is the snapshot's wire payload, so it is new each time, sized exactly.
        List<ActorSnapshot> actors = new(_snapshotPlayerTargets.Count + enemies.Count);

        foreach (IAuthoritativePlayerTarget playerTarget in _snapshotPlayerTargets)
        {
            string displayName = TryResolveDisplayName(playerTarget.SessionId);
            actors.Add(new ActorSnapshot(
                playerTarget.ActorId,
                CreatureKind.Player,
                playerTarget.SessionId.Value,
                playerTarget.Position,
                playerTarget.Facing,
                playerTarget.Combatant.Health.CurrentHealth,
                playerTarget.Combatant.Health.MaxHealth,
                playerTarget.IsAlive,
                playerTarget.InteractionMode,
                PlayerCombatStatsSnapshot.From(playerTarget.Combatant.BaseStats),
                displayName,
                new PlayerProgressionSnapshot(
                    playerTarget.Combatant.Level,
                    playerTarget.Combatant.Experience,
                    EvaluateSnapshotExperienceToNextLevel(playerTarget.Combatant))));
        }

        _snapshotPlayerTargets.Clear();
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyActor actor = enemies[i];
            actors.Add(new ActorSnapshot(
                actor.Combatant.Actor.Id,
                CreatureKind.Enemy,
                null,
                actor.Position,
                actor.Forward,
                actor.Combatant.Health.CurrentHealth,
                actor.Combatant.Health.MaxHealth,
                actor.Combatant.Health.IsAlive,
                null,
                null,
                actor.DisplayName,
                null,
                actor.BehaviorState));
        }

        return actors;
    }

    // The formula service builds a variables dictionary per evaluation, and every snapshot asks
    // again for every player. The service is immutable, so the answer for an actor's unchanged
    // level and experience is reused.
    private int EvaluateSnapshotExperienceToNextLevel(PlayerCombatant combatant)
    {
        int actorId = combatant.Actor.Id;
        int level = combatant.Level;
        int experience = combatant.Experience;
        if (_experienceToNextLevelByActor.TryGetValue(actorId, out (int Level, int Experience, int Required) cached)
            && cached.Level == level
            && cached.Experience == experience)
        {
            return cached.Required;
        }

        int required = _combatFormulaService.EvaluateExperienceToNextLevel(level, experience);
        _experienceToNextLevelByActor[actorId] = (level, experience, required);
        return required;
    }

    public WorldSnapshot ExportWorldSnapshot()
    {
        List<Vector3> placedCubeCells = [];
        List<PlacedCubeSnapshot> placedCubes = [];
        List<Vector3> fracturedCubeCells = [];
        List<FracturedCubeSnapshot> fracturedCubes = [];
        List<Vector3> authoredSubcellCells = [];

        foreach (GeometryCellRecord cell in Map.GeometryCells.Values)
        {
            if (cell.Key.Y == 0)
            {
                continue;
            }

            Vector3 cellPosition = new(cell.Key.X, cell.Key.Y, cell.Key.Z);
            if (cell.SubdivisionState == null)
            {
                Cube? cube = Map.TryGetCubeAtCell(cell.Key.X, cell.Key.Y, cell.Key.Z);
                Vector3 centerPosition = cube?.Position ??
                    new Vector3(cell.Key.X + 0.5f, cell.Key.Y + 0.5f, cell.Key.Z + 0.5f);
                Vector3 authoritativeCell = ToCellPosition(centerPosition);
                placedCubeCells.Add(authoritativeCell);
                placedCubes.Add(new PlacedCubeSnapshot(
                    authoritativeCell,
                    centerPosition,
                    cell.Definition.MaterialId,
                    cube != null ? _worldObjects.TryGet(cube)?.Actor.Id : null));
            }
            else
            {
                authoredSubcellCells.Add(cellPosition);
            }
        }

        foreach (GeometryCellRecord rubble in Map.RubbleCells.Values)
        {
            Vector3 originCell = new(rubble.Key.X, rubble.Key.Y, rubble.Key.Z);
            fracturedCubeCells.Add(originCell);
            fracturedCubes.Add(new FracturedCubeSnapshot(
                originCell,
                rubble.Definition.MaterialId,
                rubble.FracturePieces!
                    .Where(GeometryFracturePiece.IsValid)
                    .ToArray()));
        }

        return new WorldSnapshot(
            Map.Width,
            Map.Height,
            Map.Depth,
            placedCubeCells,
            fracturedCubeCells,
            authoredSubcellCells,
            placedCubes,
            fracturedCubes,
            LastWorldMutationSequence);
    }

    public AuthoritativeActionResult ProcessAuthoritativeAction(PlayerActionMessage message)
    {
        if (!TryResolveActionContext(message.SessionId, out AuthoritativeSessionActionContext? context))
        {
            return CreateRejectedActionResult(message, "Unknown player session.");
        }

        ArgumentNullException.ThrowIfNull(context);

        if (!context.Player.IsSpawned)
        {
            return CreateRejectedActionResult(message, "Player session is not spawned in the authoritative world.");
        }

        if (!_sessions.TryAcceptAction(message.SessionId, message.ActionSequence, out _))
        {
            return CreateRejectedActionResult(message, "Action sequence is stale or has already been accepted.");
        }

        return message.Payload switch
        {
            BuildUseIntent buildIntent when context.SupportsBuildActions
                => ProcessBuildUseAction(
                    message,
                    buildIntent,
                    context.InteractionMode,
                    context.CombatRayOrigin),
            BuildUseIntent
                => CreateRejectedActionResult(
                    message,
                    "This player session is not yet attached to authoritative build ownership."),
            CombatUseIntent combatIntent when context.SupportsCombatActions
                => ProcessCombatUseAction(
                    message,
                    ResolveAuthoritativeCombatIntent(context, combatIntent),
                    context.InteractionMode,
                    context.Combatant),
            InventoryEquipIntent equipIntent
                => ProcessInventoryEquipAction(message, context.Player, equipIntent),
            PlayerStatsUpdateIntent statsIntent
                => ProcessPlayerStatsUpdateAction(message, context.Combatant, statsIntent),
            CombatUseIntent
                => CreateRejectedActionResult(
                    message,
                    "This player session is not yet attached to full authoritative combat ownership."),
            _ => CreateRejectedActionResult(message, $"Unsupported action payload '{message.Payload.GetType().Name}'.")
        };
    }

    public void AdvanceSecondaryRemoteSession(
        PlayerCommandFrame commandFrame,
        float deltaTime,
        PlayerSimulationTuning playerTuning)
    {
        if (!_sessions.TryAcceptCommand(commandFrame.SessionId, commandFrame.Sequence, out ConnectedPlayer? player)
            || player == null)
        {
            return;
        }

        if (!_secondaryRemotePlayers.TryGetValue(commandFrame.SessionId, out RemoteSessionPlayerState? actor))
        {
            return;
        }

        if (commandFrame.RequestedMode.Mode.HasValue)
        {
            SetSessionInteractionMode(commandFrame.SessionId, commandFrame.RequestedMode.Mode.Value);
        }

        // Movement is simulated in the next tick before the physics step, together with every
        // other session; deltaTime and tuning are applied there.
        _ = deltaTime;
        _ = playerTuning;
        actor.ApplyCommand(commandFrame);
    }

    // Counter-based: the rolls are a pure function of (simulation seed, tick, session, action
    // sequence, target), so they do not depend on the order actions are processed in a tick, and a
    // replay of the same actions reproduces them. The target is the hit actor's id: the enemy's, or
    // the cube's world-object actor id (stable for the cube's life, unlike its cell once it moves).
    private PlayerAttackResolution ResolveSeededAttack(
        PlayerCombatant combatant,
        PlayerActionMessage message,
        ulong targetSeed)
    {
        DeterministicRandom rng = new(Seed.Combine(
            SimulationSeed,
            CurrentServerTick,
            StableHash.Of(message.SessionId.Value),
            message.ActionSequence,
            targetSeed));
        return combatant.ResolveAttack(
            () => rng.NextInt(
                combatant.DerivedStats.MinDamage,
                combatant.DerivedStats.MaxDamage + 1),
            rng.NextDouble);
    }

    private void ApplyInteractionModeSelection(PlayerSessionId sessionId, PlayerInteractionCommand command)
    {
        if (command.SelectBuildingModePressed)
        {
            SetSessionInteractionMode(sessionId, PlayerInteractionMode.Building);
        }
        else if (command.SelectCombatModePressed)
        {
            SetSessionInteractionMode(sessionId, PlayerInteractionMode.Combat);
        }
    }

    private AuthoritativeActionResult ProcessCombatUseAction(
        PlayerActionMessage message,
        CombatUseIntent intent,
        PlayerInteractionMode interactionMode,
        PlayerCombatant combatant)
    {
        if (interactionMode != PlayerInteractionMode.Combat)
        {
            return CreateRejectedActionResult(message, "Combat action rejected because the authoritative mode is not Combat.");
        }

        if (!combatant.TryStartAttack())
        {
            return CreateRejectedActionResult(message, "Combat action rejected because attack is on cooldown.");
        }

        EnemyActor? enemy = CombatUseResolver.TryResolveEnemyHit(
            _enemies,
            intent.RayOrigin,
            intent.RayDirection,
            CombatUseResolver.DefaultHitRange);
        if (enemy != null)
        {
            PlayerAttackResolution attack = ResolveSeededAttack(
                combatant,
                message,
                Seed.FromInt(enemy.Combatant.Actor.Id));
            DamageReceipt receipt = enemy.Combatant.ApplyDamage(attack.Damage);
            if (receipt.AppliedAmount > 0)
            {
                _events.Publish(new DamageAppliedEvent(
                    enemy.Combatant.Actor,
                    receipt.AppliedAmount,
                    attack.FloatingTextKind));
            }

            if (receipt.WasFatal)
            {
                AwardEnemyKillExperience(message.SessionId, enemy.Level);
                DeletePersistedEnemy(enemy.Combatant.Actor.Id);
                _enemies.PruneInactive();
            }

            return new AuthoritativeActionResult(
                message.SessionId.Value,
                message.ActionSequence,
                CurrentServerTick,
                Accepted: true,
                Payload: new CombatResolutionResult(
                    enemy.Combatant.Actor.Id,
                    enemy.Combatant.Actor.Kind,
                    receipt.AppliedAmount,
                    receipt.WasFatal,
                    enemy.Combatant.Health.CurrentHealth,
                    enemy.Combatant.Health.MaxHealth));
        }

        WorldObjectRecord? record = CombatUseResolver.TryResolveDestructibleCubeHit(
            Map,
            _worldObjects,
            intent.RayOrigin,
            intent.RayDirection,
            CombatUseResolver.DefaultHitRange);
        if (record == null)
        {
            return new AuthoritativeActionResult(
                message.SessionId.Value,
                message.ActionSequence,
                CurrentServerTick,
                Accepted: true,
                Payload: new CombatResolutionResult(
                    TargetActorId: null,
                    TargetKind: null,
                    DamageApplied: 0,
                    TargetDied: false,
                    CurrentHealth: 0,
                    MaxHealth: 0));
        }

        record = RepairCubeRecordFromPersistence(record);

        PlayerAttackResolution cubeAttack = ResolveSeededAttack(
            combatant,
            message,
            Seed.FromInt(record.Actor.Id));
        DamageReceipt cubeReceipt = record.ApplyDamage(cubeAttack.Damage);
        if (cubeReceipt.AppliedAmount > 0)
        {
            _events.Publish(new DamageAppliedEvent(
                record.Actor,
                cubeReceipt.AppliedAmount,
                cubeAttack.FloatingTextKind));
        }

        if (!cubeReceipt.WasFatal)
        {
            return new AuthoritativeActionResult(
                message.SessionId.Value,
                message.ActionSequence,
                CurrentServerTick,
                Accepted: true,
                Payload: new CombatResolutionResult(
                    record.Actor.Id,
                    record.Actor.Kind,
                    cubeReceipt.AppliedAmount,
                    cubeReceipt.WasFatal,
                    record.Health.CurrentHealth,
                    record.Health.MaxHealth));
        }

        _events.Publish(new ActorDiedEvent(record.Actor));
        object sourceObject = record.SourceObject;
        if (sourceObject is Cube destroyedCube)
        {
            if (Map.TryGetCellForCube(destroyedCube, out GeometryCellKey cell) &&
                Map.FractureCubeIntoPieces(cell))
            {
                GeometryCellRecord? fracturedCell = Map.TryGetRubbleAtCell(cell);
                GeometryMaterialId materialId = fracturedCell?.Definition.MaterialId ?? GeometryMaterialId.Grass;
                GeometryFracturePiece[] fracturePieces = Map.CaptureFracturePieces()
                    .Where(piece => piece.Cell == cell)
                    .Select(piece => piece.Piece)
                    .Where(GeometryFracturePiece.IsValid)
                    .ToArray();
                DeletePersistedPlacedCube(record.Actor.Id);
                PersistFracturePieces(record, cell);
                _worldObjects.Unregister(sourceObject, preserveDetachedRecord: false);
                _events.Publish(new CubeFracturedEvent(
                    destroyedCube,
                    new Vector3(cell.X, cell.Y, cell.Z),
                    materialId,
                    fracturePieces));
            }
            else if (_worldObjects.TryRemove(record))
            {
                DeletePersistedPlacedCube(record.Actor.Id);
                _events.Publish(new CubeDestroyedEvent(
                    destroyedCube,
                    new Vector3(
                        MathF.Floor(destroyedCube.Position.X + 1e-6f),
                        MathF.Floor(destroyedCube.Position.Y + 1e-6f),
                        MathF.Floor(destroyedCube.Position.Z + 1e-6f)),
                    record.Actor.Id));
            }
        }
        else
        {
            DeletePersistedPlacedCube(record.Actor.Id);
            _worldObjects.TryRemove(record);
        }

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new CombatResolutionResult(
                record.Actor.Id,
                record.Actor.Kind,
                cubeReceipt.AppliedAmount,
                cubeReceipt.WasFatal,
                record.Health.CurrentHealth,
                record.Health.MaxHealth));
    }

    public IPersistenceCheckpointWriter? CheckpointWriter => _checkpointWriter;

    // Synchronous capture-and-write. The dedicated server instead captures under its lock and
    // writes on a background worker; see DedicatedServerHost.
    public void PersistCheckpoint()
    {
        PersistenceCheckpoint? checkpoint = CapturePersistenceCheckpoint();
        if (checkpoint != null)
        {
            _checkpointWriter?.Write(checkpoint);
        }
    }

    // Copies the volatile authoritative state without touching the database, so it is cheap
    // enough to run on the tick thread.
    public PersistenceCheckpoint? CapturePersistenceCheckpoint()
    {
        if (_worldPersistence == null)
        {
            return null;
        }

        DateTime now = DateTime.UtcNow;
        List<PersistedPlayerWorldState> worldStates = [];
        List<PlayerCheckpointVitals> vitals = [];
        foreach (ConnectedPlayer player in _sessions.Players.Where(player => !player.IsLocal))
        {
            if (!TryResolveAuthoritativePlayerTarget(player.SessionId, out _, out IAuthoritativePlayerTarget? target)
                || target == null)
            {
                continue;
            }

            vitals.Add(new PlayerCheckpointVitals(
                player.DisplayName,
                GetSessionInteractionMode(player.SessionId),
                target.Combatant.Health.CurrentHealth,
                target.Combatant.Health.MaxHealth));
            worldStates.Add(new PersistedPlayerWorldState(
                player.DisplayName,
                target.Position,
                target.Facing,
                CurrentHealth: target.Combatant.Health.CurrentHealth,
                CurrentEnergy: 0,
                UpdatedUtc: now));
        }

        PersistedEnemyInstance[] enemies = _enemies.ActiveActors
            .Where(enemy => enemy.IsActive)
            .Select(enemy => CreatePersistedEnemy(enemy, now))
            .ToArray();

        return new PersistenceCheckpoint(
            worldStates,
            vitals,
            enemies,
            CapturePlacedCubeCheckpoint(now),
            CaptureFracturePieceCheckpoint(now));
    }

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

    private void SpawnInitialWolf()
    {
        Vector3 spawnPosition = WolfSpawnPolicy.ResolveInitialSpawn(Map.Width, Map.Depth);

        EnemyCombatant combatant = new(
            () => spawnPosition,
            _events,
            actorId: 2000,
            maxHealth: 100,
            attackDamage: 12,
            attackIntervalSeconds: 1.05f,
            anchorHeightOffset: WolfDefinition.CombatAnchorHeight);
        EnemyActor wolf = new(
            combatant,
            spawnPosition,
            aggroRange: 10f,
            attackRange: 1.35f,
            moveSpeed: 2.8f,
            preferredStandOffDistance: WolfDefinition.PreferredStandOffDistance,
            collisionRadius: WolfDefinition.CollisionRadius,
            collisionHeight: WolfDefinition.CollisionHeight,
            displayName: "Wolf",
            enemyKind: "Wolf",
            level: 1);
        _enemies.Add(wolf);
        PersistEnemy(wolf);
    }

    private void EnsureWolfExists()
    {
        if (_enemies.HasActiveActors)
        {
            return;
        }

        SpawnInitialWolf();
    }

    private void ResolvePlayerEnemySeparation(List<IAuthoritativePlayerTarget> playerTargets)
    {
        IReadOnlyList<EnemyActor> enemies = _enemies.ActiveActors;
        foreach (IAuthoritativePlayerTarget playerTarget in playerTargets)
        {
            if (playerTarget.ActorId == Player.Combatant.Actor.Id && Player.IsNoclipEnabled)
            {
                continue;
            }

            Vector3 resolvedPosition = playerTarget.Position;
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyActor actor = enemies[i];
                if (!actor.IsActive)
                {
                    continue;
                }

                resolvedPosition = EnemyPlayerCollisionResolver.ResolvePlayerPosition(
                    resolvedPosition,
                    actor.Position,
                    WolfDefinition.PlayerSeparationDistance,
                    -actor.Forward);
            }

            if ((resolvedPosition - playerTarget.Position).LengthSquared > 0.000001f)
            {
                playerTarget.SetWorldPosition(resolvedPosition);
            }
        }
    }

    private PlayerSimulationInput CreatePlayerSimulationInput(PlayerCommandFrame commandFrame)
    {
        return new PlayerSimulationInput(
            PlayerMovementResolver.CreateMovementCommand(Player.Camera, Player.IsNoclipEnabled, commandFrame.MoveIntent),
            new Vector2(commandFrame.LookIntent.YawDelta, commandFrame.LookIntent.PitchDelta),
            AllowGameplayInput: true,
            AllowLook: true);
    }

    private void ApplyCommandFacing(PlayerCommandFrame commandFrame)
    {
        if (commandFrame.LookIntent.Facing.HasValue)
        {
            Player.SetFacing(commandFrame.LookIntent.Facing.Value);
        }
    }

    public PlayerActionMessage? TryCreateCommandPrimaryUseActionMessage(PlayerCommandFrame commandFrame)
    {
        if (!TryResolveActionContext(commandFrame.SessionId, out AuthoritativeSessionActionContext? context) ||
            context == null)
        {
            return null;
        }

        return context.InteractionMode switch
        {
            PlayerInteractionMode.Building => PlayerInteraction.TryCreateBuildUseRequest(
                    context.CombatRayOrigin,
                    context.CombatRayDirection,
                    out BuildUseRequest buildUseRequest)
                ? new PlayerActionMessage(
                    commandFrame.SessionId,
                    commandFrame.Sequence,
                    new BuildUseIntent(buildUseRequest.PlacementTarget))
                : null,
            PlayerInteractionMode.Combat => new PlayerActionMessage(
                commandFrame.SessionId,
                commandFrame.Sequence,
                new CombatUseIntent(context.CombatRayOrigin, context.CombatRayDirection)),
            _ => null
        };
    }

    private AuthoritativeActionResult ProcessBuildUseAction(
        PlayerActionMessage message,
        BuildUseIntent intent,
        PlayerInteractionMode interactionMode,
        Vector3 authoritativeOrigin)
    {
        if (interactionMode != PlayerInteractionMode.Building)
        {
            return CreateRejectedActionResult(message, "Build action rejected because the authoritative mode is not Building.");
        }

        if ((intent.PlacementTarget - authoritativeOrigin).Length > MaxAuthoritativeBuildDistance)
        {
            return CreateRejectedActionResult(message, "Build placement rejected because the target is out of range.");
        }

        GeometryMaterialId placementMaterial = ResolveBuildPlacementMaterial(intent.PlacementTarget);
        Cube? placedCube = Map.TryAddCube(
            intent.PlacementTarget,
            materialId: placementMaterial);
        if (placedCube == null || placedCube.Position.Y <= 0f)
        {
            return CreateRejectedActionResult(message, "Build placement rejected by authoritative validation.");
        }

        string ownerPlayerName = TryResolveDisplayName(message.SessionId);
        WorldObjectRecord record = _worldObjects.RegisterCube(
            placedCube,
            Map,
            actorId: null,
            ownerPlayerName: ownerPlayerName);
        PersistPlacedCube(record, placementMaterial);
        Vector3 placedCell = ToCellPosition(placedCube.Position);
        _events.Publish(new CubePlacedEvent(placedCube, placedCell, placementMaterial));

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new WorldMutationResult(
                WorldMutationKind.CubePlaced,
                placedCell,
                Applied: true));
    }

    private AuthoritativeActionResult ProcessPlayerStatsUpdateAction(
        PlayerActionMessage message,
        PlayerCombatant combatant,
        PlayerStatsUpdateIntent intent)
    {
        combatant.ApplyBaseStats(intent.Stats.ToCombatStats());
        PersistPlayerProfile(message.SessionId, combatant);

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new PlayerStatsUpdateResult(PlayerCombatStatsSnapshot.From(combatant.BaseStats)));
    }

    private AuthoritativeActionResult ProcessInventoryEquipAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        InventoryEquipIntent intent)
    {
        if (intent.SlotIndex < 0 || intent.SlotIndex >= InventoryConstants.BackpackSlotCount)
        {
            return CreateRejectedActionResult(message, "Inventory equip rejected because the slot index is out of range.");
        }

        EnsurePlayerInventory(player.DisplayName);
        if (!_inventorySlotsByPlayer.TryGetValue(player.DisplayName, out Dictionary<int, long>? slots)
            || !slots.TryGetValue(intent.SlotIndex, out long itemInstanceId)
            || !_itemInstances.ContainsKey(itemInstanceId))
        {
            return CreateRejectedActionResult(message, "Inventory equip rejected because the slot is empty.");
        }

        slots[InventoryConstants.EquippedSlotIndex] = itemInstanceId;
        PersistInventorySlot(player.DisplayName, InventoryConstants.EquippedSlotIndex, itemInstanceId);
        RefreshAuthoritativePlayerCombatStats(message.SessionId);

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new InventoryChangedResult(BuildInventorySnapshot(player.DisplayName)));
    }

    private AuthoritativeActionResult CreateRejectedActionResult(PlayerActionMessage message, string reason)
    {
        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: false,
            Payload: null,
            Rejection: new RejectedUseDelta(reason));
    }

    // Sequences let clients skip deltas already folded into the join-time world snapshot.
    private void QueueWorldMutation(WorldMutationDelta mutation)
    {
        LastWorldMutationSequence++;
        _pendingWorldMutations.Add(mutation with { Sequence = LastWorldMutationSequence });
    }

    private void SubscribeReplicationExports()
    {
        _subscriptions.Add(_events.Subscribe<CubePlacedEvent>(evt =>
        {
            QueueWorldMutation(new WorldMutationDelta(
                WorldMutationKind.CubePlaced,
                evt.CellPosition,
                MaterialId: evt.MaterialId,
                ActorId: _worldObjects.TryGet(evt.Cube)?.Actor.Id));
        }));

        _subscriptions.Add(_events.Subscribe<CubeDestroyedEvent>(evt =>
        {
            QueueWorldMutation(new WorldMutationDelta(
                WorldMutationKind.CubeDestroyed,
                evt.CellPosition,
                ActorId: evt.ActorId));
        }));

        _subscriptions.Add(_events.Subscribe<CubeFracturedEvent>(evt =>
        {
            QueueWorldMutation(WorldMutationDelta.Fractured(
                evt.CellPosition,
                evt.MaterialId,
                evt.Pieces));
        }));

        _subscriptions.Add(_events.Subscribe<DamageAppliedEvent>(evt =>
        {
            _pendingDamageEvents.Add(new DamageEventDelta(
                evt.Actor.Id,
                evt.Actor.Kind,
                evt.Amount,
                ActorDied: !evt.Actor.Damageable.Health.IsAlive,
                evt.Actor.Damageable.Health.CurrentHealth,
                evt.Actor.Damageable.Health.MaxHealth));
        }));

        _subscriptions.Add(_events.Subscribe<ActorDiedEvent>(evt =>
        {
            _pendingDamageEvents.Add(new DamageEventDelta(
                evt.Actor.Id,
                evt.Actor.Kind,
                Amount: 0,
                ActorDied: true,
                evt.Actor.Damageable.Health.CurrentHealth,
                evt.Actor.Damageable.Health.MaxHealth));
        }));
    }

    private static Vector3 ToCellPosition(Vector3 position)
    {
        return new Vector3(
            MathF.Floor(position.X + 1e-6f),
            MathF.Floor(position.Y + 1e-6f),
            MathF.Floor(position.Z + 1e-6f));
    }

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

    private void RestorePersistedInventoryState()
    {
        if (_worldPersistence == null)
        {
            return;
        }

        foreach (PersistedItemInstance item in _worldPersistence.LoadItemInstances())
        {
            _itemInstances[item.ItemInstanceId] = item;
            _nextItemInstanceId = Math.Max(_nextItemInstanceId, item.ItemInstanceId + 1);
        }

        foreach (PersistedInventorySlot slot in _worldPersistence.LoadInventorySlots())
        {
            GetOrCreateInventorySlots(slot.PlayerName)[slot.SlotIndex] = slot.ItemInstanceId;
        }
    }

    private void EnsurePlayerInventory(string playerName)
    {
        Dictionary<int, long> slots = GetOrCreateInventorySlots(playerName);
        bool hasBackpackContent = slots.Keys.Any(slotIndex => slotIndex >= 0 && slotIndex < InventoryConstants.BackpackSlotCount);
        if (hasBackpackContent)
        {
            if (!slots.ContainsKey(InventoryConstants.EquippedSlotIndex))
            {
                KeyValuePair<int, long> firstOccupiedSlot = slots
                    .Where(entry => entry.Key >= 0 && entry.Key < InventoryConstants.BackpackSlotCount)
                    .OrderBy(entry => entry.Key)
                    .First();
                slots[InventoryConstants.EquippedSlotIndex] = firstOccupiedSlot.Value;
                PersistInventorySlot(playerName, InventoryConstants.EquippedSlotIndex, firstOccupiedSlot.Value);
            }

            return;
        }

        SeedStarterInventory(playerName, slots);
    }

    private void RefreshAuthoritativePlayerCombatStats(PlayerSessionId sessionId)
    {
        if (!TryResolveAuthoritativePlayerTarget(sessionId, out ConnectedPlayer? player, out IAuthoritativePlayerTarget? target)
            || player == null
            || target == null)
        {
            return;
        }

        target.Combatant.ApplyEquippedToolModifier(ResolveEquippedToolModifier(player.DisplayName));
    }

    private void SeedStarterInventory(string playerName, Dictionary<int, long> slots)
    {
        int slotIndex = 0;
        long? firstItemInstanceId = null;
        foreach (string itemKind in ToolCatalog.StarterItemKinds)
        {
            ToolDefinition definition = ToolCatalog.Resolve(itemKind);
            long itemInstanceId = _nextItemInstanceId++;
            firstItemInstanceId ??= itemInstanceId;
            PersistedItemInstance item = new(
                itemInstanceId,
                definition.ItemKind,
                definition.DisplayName,
                definition.VisualId,
                playerName,
                WorldPosition: null,
                State: "Inventory",
                StackCount: 1,
                CreatedUtc: DateTime.UtcNow,
                UpdatedUtc: DateTime.UtcNow);
            _itemInstances[item.ItemInstanceId] = item;
            slots[slotIndex] = itemInstanceId;
            EnqueueWorldWrite(world => world.SaveItemInstance(item));
            PersistInventorySlot(playerName, slotIndex, itemInstanceId);
            slotIndex++;
        }

        if (firstItemInstanceId.HasValue)
        {
            slots[InventoryConstants.EquippedSlotIndex] = firstItemInstanceId.Value;
            PersistInventorySlot(playerName, InventoryConstants.EquippedSlotIndex, firstItemInstanceId.Value);
        }
    }

    private PlayerInventorySnapshot BuildInventorySnapshot(string playerName)
    {
        Dictionary<int, long> slots = GetOrCreateInventorySlots(playerName);
        List<InventorySlotSnapshot> backpack = [];
        for (int slotIndex = 0; slotIndex < InventoryConstants.BackpackSlotCount; slotIndex++)
        {
            InventoryItemSnapshot? item = TryResolveInventoryItem(slots, slotIndex);
            bool equipped = slots.TryGetValue(InventoryConstants.EquippedSlotIndex, out long equippedItemId)
                && item.HasValue
                && item.Value.ItemInstanceId == equippedItemId;
            backpack.Add(new InventorySlotSnapshot(slotIndex, item, equipped));
        }

        InventoryItemSnapshot? equippedItem = TryResolveInventoryItem(slots, InventoryConstants.EquippedSlotIndex);
        return new PlayerInventorySnapshot(
            playerName,
            InventoryConstants.GridColumns,
            InventoryConstants.GridRows,
            backpack,
            new InventorySlotSnapshot(InventoryConstants.EquippedSlotIndex, equippedItem, equippedItem.HasValue));
    }

    private InventoryItemSnapshot? TryResolveInventoryItem(Dictionary<int, long> slots, int slotIndex)
    {
        if (!slots.TryGetValue(slotIndex, out long itemInstanceId)
            || !_itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item))
        {
            return null;
        }

        ToolDefinition definition = ToolCatalog.Resolve(item.ItemKind);
        return new InventoryItemSnapshot(
            item.ItemInstanceId,
            item.ItemKind,
            item.DisplayName,
            item.VisualId,
            item.StackCount,
            definition.Description,
            definition.DetailLines);
    }

    private ToolStatModifier ResolveEquippedToolModifier(string playerName)
    {
        if (!_inventorySlotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots)
            || !slots.TryGetValue(InventoryConstants.EquippedSlotIndex, out long itemInstanceId)
            || !_itemInstances.TryGetValue(itemInstanceId, out PersistedItemInstance item))
        {
            return default;
        }

        return ToolCatalog.Resolve(item.ItemKind).StatModifier;
    }

    private Dictionary<int, long> GetOrCreateInventorySlots(string playerName)
    {
        if (!_inventorySlotsByPlayer.TryGetValue(playerName, out Dictionary<int, long>? slots))
        {
            slots = [];
            _inventorySlotsByPlayer[playerName] = slots;
        }

        return slots;
    }

    private void PersistInventorySlot(string playerName, int slotIndex, long itemInstanceId)
    {
        PersistedInventorySlot slot = new(
            playerName,
            slotIndex,
            itemInstanceId,
            DateTime.UtcNow);
        EnqueueWorldWrite(world => world.SaveInventorySlot(slot));
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
        foreach (PersistedEnemyInstance enemy in enemies)
        {
            _enemies.Add(CreatePersistedEnemy(enemy));
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
            || player.IsLocal)
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

    private void AwardEnemyKillExperience(PlayerSessionId sessionId, int enemyLevel)
    {
        if (!TryResolveAuthoritativePlayerTarget(sessionId, out _, out IAuthoritativePlayerTarget? playerTarget)
            || playerTarget == null)
        {
            return;
        }

        PlayerCombatant combatant = playerTarget.Combatant;
        int reward = _combatFormulaService.EvaluateEnemyExperienceReward(enemyLevel, combatant.Level);
        if (reward <= 0)
        {
            return;
        }

        int level = combatant.Level;
        int experience = combatant.Experience + reward;
        int required = _combatFormulaService.EvaluateExperienceToNextLevel(level, experience);
        while (experience >= required)
        {
            experience -= required;
            level++;
            required = _combatFormulaService.EvaluateExperienceToNextLevel(level, experience);
        }

        combatant.ApplyProgression(level, experience);
        PersistPlayerProfile(sessionId, combatant);
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

    // The previous per-cube checkpoint path repaired identities from the database, reloading
    // the whole placed-cube table and querying profiles once per cube on the tick thread. The
    // capture uses registry identity only; records without a persistence owner are left to the
    // placement/combat paths that still repair identity before writing.
    private IReadOnlyList<PersistedPlacedCube> CapturePlacedCubeCheckpoint(DateTime now)
    {
        List<PersistedPlacedCube> cubes = [];
        foreach (WorldObjectRecord record in _worldObjects.GetCubeRecords())
        {
            if (record.SourceObject is not Cube cube ||
                string.IsNullOrWhiteSpace(record.OwnerPlayerName) ||
                !Map.TryGetCellForCube(cube, out GeometryCellKey cell) ||
                cell.Y == 0)
            {
                continue;
            }

            GeometryCellRecord? geometryCell = Map.TryGetGeometryCellAtCell(cell.X, cell.Y, cell.Z);
            PersistedPlacedCube captured = new(
                record.Actor.Id,
                record.OwnerPlayerName,
                new Vector3(cell.X, cell.Y, cell.Z),
                GeometryShapeKind.SolidCube,
                geometryCell?.Definition.MaterialId ?? GeometryMaterialId.Grass,
                record.Health.CurrentHealth,
                record.Health.MaxHealth,
                now,
                now,
                cube.Position);
            cubes.Add(captured);
            if (_persistedCubes.ContainsKey(captured.CubeId))
            {
                _persistedCubes[captured.CubeId] = captured;
            }
        }

        return cubes;
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

    private IReadOnlyList<PersistedCubeFracturePiece> CaptureFracturePieceCheckpoint(DateTime now)
    {
        if (_persistedFractureGroups.Count == 0)
        {
            return [];
        }

        // One map scan for all groups instead of one full scan per fractured cell.
        List<PersistedCubeFracturePiece> pieces = [];
        foreach ((GeometryCellKey PieceCell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId) in
            Map.CaptureFracturePieces())
        {
            if (!_persistedFractureGroups.TryGetValue(PieceCell, out PersistedFractureGroup group))
            {
                continue;
            }

            pieces.Add(new PersistedCubeFracturePiece(
                SourceCubeId: group.SourceCubeId,
                PieceIndex: PieceIndex,
                OwnerPlayerName: group.OwnerPlayerName,
                ParentCell: new Vector3(PieceCell.X, PieceCell.Y, PieceCell.Z),
                Center: Piece.Center,
                SideLength: Piece.SideLength,
                MaterialId: MaterialId,
                CreatedUtc: now,
                UpdatedUtc: now));
        }

        return pieces;
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

        bool identityMatches =
            record.Actor.Id == persistedCube.CubeId &&
            string.Equals(record.OwnerPlayerName, persistedCube.OwnerPlayerName, StringComparison.OrdinalIgnoreCase) &&
            record.Health.CurrentHealth == persistedCube.CurrentHealth &&
            record.Health.MaxHealth == persistedCube.MaxHealth;
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

    private GeometryMaterialId ResolveBuildPlacementMaterial(Vector3 placementTarget)
    {
        Vector3 belowCell = new(
            MathF.Floor(placementTarget.X + 1e-6f),
            MathF.Floor(placementTarget.Y - 1f + 1e-6f),
            MathF.Floor(placementTarget.Z + 1e-6f));
        GeometryCellRecord? supportCell = Map.TryGetGeometryCellAtCell((int)belowCell.X, (int)belowCell.Y, (int)belowCell.Z);
        return supportCell?.Definition.MaterialId ?? GeometryMaterialId.Grass;
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

    private bool TryResolveActionContext(
        PlayerSessionId sessionId,
        out AuthoritativeSessionActionContext? context)
    {
        context = null;
        if (!TryResolveAuthoritativePlayerTarget(sessionId, out ConnectedPlayer? player, out IAuthoritativePlayerTarget? playerTarget)
            || player == null
            || playerTarget == null)
        {
            return false;
        }

        context = new AuthoritativeSessionActionContext(
            player,
            playerTarget.Combatant,
            playerTarget.InteractionMode,
            playerTarget.EyePosition,
            playerTarget.Facing,
            SupportsBuildActions: true,
            SupportsCombatActions: true);
        return true;
    }

    private int AllocateRemoteActorId()
    {
        int actorId = Math.Max(_nextRemoteActorId, Player.Combatant.Actor.Id + 1);
        _nextRemoteActorId = actorId + 1;
        return actorId;
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

    private static PlayerCommandFrame CreateIdleCommandFrame(PlayerSessionId sessionId, ulong sequence)
    {
        return new PlayerCommandFrame(
            sessionId,
            sequence,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
            new PlayerMoveIntent(0f, 0f, 0f),
            new PlayerLookIntent(0f, 0f),
            PrimaryUsePressed: false);
    }

    private static CombatUseIntent ResolveAuthoritativeCombatIntent(
        AuthoritativeSessionActionContext context,
        CombatUseIntent clientIntent)
    {
        _ = clientIntent;
        return new CombatUseIntent(context.CombatRayOrigin, context.CombatRayDirection);
    }

    private string TryResolveDisplayName(PlayerSessionId sessionId)
    {
        return _sessions.TryGet(sessionId, out ConnectedPlayer? player) && player != null
            ? player.DisplayName
            : sessionId.Value;
    }

    // Lists the players in the order every per-tick consumer depends on (nearest-target ties, the
    // snapshot's actor order): the primary seat first, unless it is the unoccupied local seat while
    // a remote player is spawned, then the spawned secondary seats in ordinal session-id order.
    private void FillAuthoritativePlayerTargets(List<IAuthoritativePlayerTarget> targets, bool spawnedAndAliveOnly)
    {
        targets.Clear();
        PlayerSessionId primarySessionId = ResolvePrimaryControlledSessionId();
        if (primarySessionId != LocalPlayerSessionId || !_sessions.HasSpawnedRemotePlayer())
        {
            AddTarget(targets, GetPrimaryAuthoritativePlayerTarget(primarySessionId), spawnedAndAliveOnly);
        }

        foreach (RemoteSessionPlayerState actor in _orderedSecondaryRemotePlayers)
        {
            if (IsListedSecondary(actor))
            {
                AddTarget(targets, actor, spawnedAndAliveOnly);
            }
        }

        static void AddTarget(List<IAuthoritativePlayerTarget> targets, IAuthoritativePlayerTarget target, bool spawnedAndAliveOnly)
        {
            if (!spawnedAndAliveOnly || (target.IsSpawned && target.IsAlive))
            {
                targets.Add(target);
            }
        }
    }

    // The same players FillAuthoritativePlayerTargets lists, searched by actor id without a list.
    private bool TryFindListedPlayerTarget(int actorId, out IAuthoritativePlayerTarget? playerTarget)
    {
        PlayerSessionId primarySessionId = ResolvePrimaryControlledSessionId();
        if (Player.Combatant.Actor.Id == actorId
            && (primarySessionId != LocalPlayerSessionId || !_sessions.HasSpawnedRemotePlayer()))
        {
            playerTarget = GetPrimaryAuthoritativePlayerTarget(primarySessionId);
            return true;
        }

        foreach (RemoteSessionPlayerState actor in _orderedSecondaryRemotePlayers)
        {
            if (actor.ActorId == actorId && IsListedSecondary(actor))
            {
                playerTarget = actor;
                return true;
            }
        }

        playerTarget = null;
        return false;
    }

    private bool IsListedSecondary(RemoteSessionPlayerState actor)
    {
        return _sessions.TryGet(actor.SessionId, out ConnectedPlayer? player)
            && player != null
            && player.IsSpawned;
    }

    private AuthoritativePlayerTarget GetPrimaryAuthoritativePlayerTarget(PlayerSessionId sessionId)
    {
        if (_primaryPlayerTarget == null || _primaryPlayerTarget.SessionId != sessionId)
        {
            _primaryPlayerTarget = CreatePrimaryAuthoritativePlayerTarget(sessionId);
        }

        return _primaryPlayerTarget;
    }

    // Created once per primary session id; every delegate reads the seat's live state.
    private AuthoritativePlayerTarget CreatePrimaryAuthoritativePlayerTarget(PlayerSessionId sessionId)
    {
        return new AuthoritativePlayerTarget(
            sessionId,
            Player.Combatant.Actor.Id,
            Player.GetWorldPosition,
            () => Player.Camera.Front,
            Player.Combatant,
            () => GetSessionInteractionMode(sessionId),
            Player.SetWorldPosition,
            () => Player.Camera.Position);
    }

    private bool TryResolveAuthoritativePlayerTarget(
        PlayerSessionId sessionId,
        out ConnectedPlayer? player,
        out IAuthoritativePlayerTarget? playerTarget)
    {
        playerTarget = null;
        if (!_sessions.TryGet(sessionId, out player) || player == null)
        {
            return false;
        }

        if (!player.IsLocal && !player.IsSpawned)
        {
            return false;
        }

        if (player.ActorId == Player.Combatant.Actor.Id && IsPrimaryControlledSession(sessionId))
        {
            playerTarget = GetPrimaryAuthoritativePlayerTarget(sessionId);
            return true;
        }

        if (_secondaryRemotePlayers.TryGetValue(sessionId, out RemoteSessionPlayerState? actor))
        {
            playerTarget = actor;
            return true;
        }

        return false;
    }
}
