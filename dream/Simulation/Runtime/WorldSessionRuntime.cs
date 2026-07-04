using OpenTK.Mathematics;
using System.Diagnostics;
using Dream.Application;
using Dream.Client.Input;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Rendering.Models;
using Dream.Server.Persistence;
using Dream.Server.Rules;
using Dream.Simulation.Combat;
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
public sealed class WorldSessionRuntime : IDisposable, IReplicatedSessionStateTarget
{
    private const float MaxAuthoritativeBuildDistance = 3f;
    private readonly bool _loadVisualAssets;
    private readonly SceneEventHub _events;
    private readonly PhysicsSyncSystem _physicsSync = new();
    private readonly WorldObjectRegistry _worldObjects;
    private readonly EnemyRegistry _enemies;
    private readonly PlayerSessionRegistry _sessions = new();
    private readonly Dictionary<PlayerSessionId, RemoteSessionPlayerState> _secondaryRemotePlayers = new();
    private readonly Dictionary<PlayerSessionId, PlayerInteractionMode> _sessionInteractionModes = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<WorldMutationDelta> _pendingWorldMutations = [];
    private readonly List<DamageEventDelta> _pendingDamageEvents = [];
    private readonly Dictionary<long, PersistedItemInstance> _itemInstances = [];
    private readonly Dictionary<string, Dictionary<int, long>> _inventorySlotsByPlayer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GeometryCellKey, PersistedFractureGroup> _persistedFractureGroups = [];
    private readonly IPlayerProfileRepository? _playerProfiles;
    private readonly IWorldPersistenceRepository? _worldPersistence;
    private readonly CombatFormulaService _combatFormulaService;
    private PlayerCommandFrame _lastAppliedCommandFrame;
    private double _lastPhysicsStepMilliseconds;
    private int _nextRemoteActorId = 2;
    private long _nextItemInstanceId = 1;

    public WorldSessionRuntime(
        int floorSize,
        Vector3 playerStartPosition,
        SceneEventHub events,
        bool loadVisualAssets = true,
        IPlayerProfileRepository? playerProfiles = null,
        IWorldPersistenceRepository? worldPersistence = null,
        CombatFormulaService? combatFormulaService = null)
    {
        _loadVisualAssets = loadVisualAssets;
        _events = events;
        _playerProfiles = playerProfiles;
        _worldPersistence = worldPersistence;
        _combatFormulaService = combatFormulaService ?? CombatFormulaService.Default;
        Physics = new PhysicsIntegrator();
        Map = new Map(Physics, loadVisualAssets: _loadVisualAssets, generateStartupTerrain: true, width: floorSize, depth: floorSize);

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

    public ConnectedPlayer AttachRemoteSession(PlayerSessionId sessionId, string? displayName = null)
    {
        if (_sessions.TryGet(sessionId, out ConnectedPlayer? existing) && existing != null)
        {
            existing.UpdateDisplayName(displayName);
            _sessionInteractionModes.TryAdd(existing.SessionId, PlayerInteractionMode.Building);
            if (!existing.IsLocal && PrimaryRemoteSessionId == null && existing.ActorId == Player.Combatant.Actor.Id)
            {
                PrimaryRemoteSessionId = existing.SessionId;
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
            PrimaryRemoteSessionId = primary.SessionId;
            return primary;
        }

        int actorId = AllocateRemoteActorId();
        Vector3 spawnPosition = ResolveSecondaryRemoteSpawnPosition(_secondaryRemotePlayers.Count);
        _secondaryRemotePlayers[sessionId] = new RemoteSessionPlayerState(
            actorId,
            sessionId,
            spawnPosition,
            combatFormulaService: _combatFormulaService);
        ConnectedPlayer remote = _sessions.Register(
            sessionId,
            actorId,
            isLocal: false,
            displayName: displayName);
        _sessionInteractionModes[remote.SessionId] = PlayerInteractionMode.Building;
        return remote;
    }

    public void RestorePersistedPlayerState(PlayerSessionId sessionId, string playerName)
    {
        if (_playerProfiles == null)
        {
            return;
        }

        PersistedPlayerProfile profile = _playerProfiles.GetOrCreate(playerName);
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
            context.AllowGameplaySimulation);
    }

    private WorldSessionUpdateResult UpdateAcceptedCommand(
        float delta,
        PlayerCommandFrame commandFrame,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation)
    {
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

        if (primaryUseTriggered)
        {
            PlayerActionMessage? actionMessage = TryCreatePrimaryUseActionMessage(commandFrame);
            if (actionMessage != null)
            {
                actionResult = ProcessAuthoritativeAction(actionMessage);
            }
        }

        Stopwatch physicsStopwatch = Stopwatch.StartNew();
        Physics.StepSimulation(delta);
        physicsStopwatch.Stop();
        _lastPhysicsStepMilliseconds = physicsStopwatch.Elapsed.TotalMilliseconds;
        Player.Update(
            delta,
            playerInput,
            playerTuning);

        if (allowGameplaySimulation)
        {
            IAuthoritativePlayerTarget[] playerTargets = EnumerateAuthoritativePlayerTargets()
                .Where(target => target.IsSpawned && target.IsAlive)
                .ToArray();
            _enemies.UpdateAll(delta, playerTargets, _events, Map);
            _enemies.PruneInactive();
            EnsureWolfExists();
            ResolvePlayerEnemySeparation(playerTargets);
        }

        _physicsSync.SyncDynamicMapBodies(Map, Physics.DynamicMapCubeBodies);
        return new WorldSessionUpdateResult(primaryUseTriggered, actionResult);
    }

    public WorldSessionUpdateResult AdvanceAuthoritativeTick(
        ulong serverTick,
        float deltaTime,
        PlayerCommandFrame commandFrame,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true)
    {
        return AdvanceAuthoritativeTick(
            serverTick,
            deltaTime,
            (PlayerCommandFrame?)commandFrame,
            playerTuning,
            allowGameplaySimulation);
    }

    public IReadOnlyList<AuthoritativeActionResult> ProcessQueuedAuthoritativeActions(
        ulong serverTick,
        IReadOnlyList<QueuedPlayerAction> queuedActions)
    {
        CurrentServerTick = serverTick;

        return queuedActions
            .OrderBy(action => action.ReceiveOrder)
            .ThenBy(action => action.SessionId.Value, StringComparer.Ordinal)
            .ThenBy(action => action.Message.ActionSequence)
            .Select(action => ProcessAuthoritativeAction(action.Message))
            .ToArray();
    }

    public WorldSessionUpdateResult AdvanceAuthoritativeTick(
        ulong serverTick,
        float deltaTime,
        PlayerCommandFrame? commandFrame,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true)
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
            allowGameplaySimulation);
    }

    public WorldSessionPresentationState CapturePresentationState(
        float maxRenderDistance,
        float presentationLeadSeconds = 0f,
        CameraViewMode cameraViewMode = CameraViewMode.FirstPerson)
    {
        Camera firstPersonCamera = Player.CreatePresentationCamera(presentationLeadSeconds);
        Camera presentationCamera = ThirdPersonCameraRig.Resolve(
            firstPersonCamera,
            Player.GetWorldPosition(),
            cameraViewMode);

        Stopwatch renderExportStopwatch = Stopwatch.StartNew();
        GeometryRenderExport visibleWorld = Map.CaptureFullGeometryRenderExport();
        renderExportStopwatch.Stop();

        return WorldSessionPresentationState.Create(
            presentationCamera,
            visibleWorld,
            _enemies.ActiveActors,
            Physics.MapCubeBodies,
            Player.PhysicsBody,
            Player.Combatant.Health,
            GetSessionInteractionMode(ResolvePrimaryControlledSessionId()),
            Player.Combatant.DerivedStats,
            Player.Combatant.Stats,
            Player.Combatant.Level,
            Player.Combatant.Experience,
            _combatFormulaService.EvaluateExperienceToNextLevel(
                Player.Combatant.Level,
                Player.Combatant.Experience),
            new FramePerformanceSnapshot(
                renderExportStopwatch.Elapsed.TotalMilliseconds,
                _lastPhysicsStepMilliseconds,
                visibleWorld.CompatibilityCubes.Count + visibleWorld.DynamicCompatibilityCubes.Count,
                visibleWorld.Blocks.Count + visibleWorld.DynamicBlocks.Count,
                0,
                0,
                0,
                0,
                Physics.MapCubeBodies.Count));
    }

    public bool TryResolveCombatActor(int actorId, out CombatActorHandle actor)
    {
        foreach (IAuthoritativePlayerTarget playerTarget in EnumerateAuthoritativePlayerTargets())
        {
            if (playerTarget.ActorId == actorId)
            {
                actor = playerTarget.Combatant.Actor;
                return true;
            }
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

        var rebuiltMap = new Map(Physics, loadVisualAssets: _loadVisualAssets, generateStartupTerrain: true, width: floorSize, depth: floorSize);
        previousMap.CopyPlacedCubesTo(rebuiltMap);
        _worldObjects.RestoreCubeSnapshot(rebuiltMap, worldObjectSnapshot);

        Map = rebuiltMap;
        Player.BindMap(rebuiltMap);
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

        Physics.Dispose();
    }

    public ServerSnapshotEnvelope ExportServerSnapshotEnvelope(bool includeWorldSnapshot = false)
    {
        List<ActorSnapshot> actors = [];

        foreach (IAuthoritativePlayerTarget playerTarget in EnumerateAuthoritativePlayerTargets())
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
                    _combatFormulaService.EvaluateExperienceToNextLevel(
                        playerTarget.Combatant.Level,
                        playerTarget.Combatant.Experience))));
        }

        foreach (EnemyActor actor in _enemies.ActiveActors)
        {
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
        Dictionary<string, ulong> acceptedSequences = new();
        foreach (ConnectedPlayer player in _sessions.Players)
        {
            if (player.LastAcceptedCommandSequence.HasValue)
            {
                acceptedSequences[player.SessionId.Value] = player.LastAcceptedCommandSequence.Value;
            }
        }

        WorldSnapshot? worldSnapshot = includeWorldSnapshot
            ? ExportWorldSnapshot()
            : null;

        ServerSnapshotEnvelope envelope = new(
            CurrentServerTick,
            acceptedSequences,
            actors,
            GetSessionInteractionMode(ResolvePrimaryControlledSessionId()),
            worldSnapshot,
            _pendingWorldMutations.ToArray(),
            _pendingDamageEvents.ToArray());

        _pendingWorldMutations.Clear();
        _pendingDamageEvents.Clear();
        return envelope;
    }

    public WorldSnapshot ExportWorldSnapshot()
    {
        List<Vector3> placedCubeCells = [];
        List<Vector3> fracturedCubeCells = [];
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
                placedCubeCells.Add(cellPosition);
            }
            else if (cell.FracturePieces == null)
            {
                authoredSubcellCells.Add(cellPosition);
            }
            else
            {
                fracturedCubeCells.Add(cellPosition);
            }
        }

        return new WorldSnapshot(
            Map.Width,
            Map.Height,
            Map.Depth,
            placedCubeCells,
            fracturedCubeCells,
            authoredSubcellCells);
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

        actor.AdvanceCombat(deltaTime);
        actor.ApplyCommand(commandFrame, playerTuning, deltaTime);
        actor.ClampToMap(Map);
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
            PlayerAttackResolution attack = combatant.ResolveAttack(
                () => Random.Shared.Next(
                    combatant.DerivedStats.MinDamage,
                    combatant.DerivedStats.MaxDamage + 1),
                () => Random.Shared.NextDouble());
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

        PlayerAttackResolution cubeAttack = combatant.ResolveAttack(
            () => Random.Shared.Next(
                combatant.DerivedStats.MinDamage,
                combatant.DerivedStats.MaxDamage + 1),
            () => Random.Shared.NextDouble());
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
                DeletePersistedPlacedCube(record.Actor.Id);
                PersistFracturePieces(record, cell);
                _worldObjects.Unregister(sourceObject);
                _events.Publish(new CubeFracturedEvent(destroyedCube, new Vector3(cell.X, cell.Y, cell.Z)));
            }
            else if (_worldObjects.TryRemove(record))
            {
                DeletePersistedPlacedCube(record.Actor.Id);
                _events.Publish(new CubeDestroyedEvent(
                    destroyedCube,
                    new Vector3(
                        MathF.Floor(destroyedCube.Position.X + 1e-6f),
                        MathF.Floor(destroyedCube.Position.Y + 1e-6f),
                        MathF.Floor(destroyedCube.Position.Z + 1e-6f))));
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

    public void PersistCheckpoint()
    {
        if (_worldPersistence == null)
        {
            return;
        }

        foreach (ConnectedPlayer player in _sessions.Players.Where(player => !player.IsLocal))
        {
            if (!TryResolveAuthoritativePlayerTarget(player.SessionId, out _, out IAuthoritativePlayerTarget? target)
                || target == null)
            {
                continue;
            }

            PersistPlayerProfile(player.SessionId, target.Combatant);
            _worldPersistence.SavePlayerWorldState(new PersistedPlayerWorldState(
                player.DisplayName,
                target.Position,
                target.Facing,
                CurrentHealth: target.Combatant.Health.CurrentHealth,
                CurrentEnergy: 0,
                UpdatedUtc: DateTime.UtcNow));
        }

        foreach (EnemyActor enemy in _enemies.ActiveActors.Where(enemy => enemy.IsActive))
        {
            PersistEnemy(enemy);
        }

        PersistFracturePieceCheckpoint();
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
            anchorHeightOffset: WolfAssetDefinition.CombatAnchorHeight);
        EnemyActor wolf = new(
            combatant,
            spawnPosition,
            aggroRange: 10f,
            attackRange: 1.35f,
            moveSpeed: 2.8f,
            preferredStandOffDistance: WolfAssetDefinition.PreferredStandOffDistance,
            collisionRadius: WolfAssetDefinition.CollisionRadius,
            collisionHeight: WolfAssetDefinition.CollisionHeight,
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

    private void ResolvePlayerEnemySeparation(IReadOnlyList<IAuthoritativePlayerTarget> playerTargets)
    {
        foreach (IAuthoritativePlayerTarget playerTarget in playerTargets)
        {
            if (playerTarget.ActorId == Player.Combatant.Actor.Id && Player.IsNoclipEnabled)
            {
                continue;
            }

            Vector3 resolvedPosition = playerTarget.Position;
            foreach (EnemyActor actor in _enemies.ActiveActors)
            {
                if (!actor.IsActive)
                {
                    continue;
                }

                resolvedPosition = EnemyPlayerCollisionResolver.ResolvePlayerPosition(
                    resolvedPosition,
                    actor.Position,
                    WolfAssetDefinition.PlayerSeparationDistance,
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
            PlayerController.CreateMovementCommand(Player.Camera, Player.IsNoclipEnabled, commandFrame.MoveIntent),
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

    private PlayerActionMessage? TryCreatePrimaryUseActionMessage(PlayerCommandFrame commandFrame)
    {
        return GetSessionInteractionMode(commandFrame.SessionId) switch
        {
            PlayerInteractionMode.Building => Player.TryCreateBuildUseRequest(true, out BuildUseRequest buildUseRequest)
                ? new PlayerActionMessage(
                    commandFrame.SessionId,
                    commandFrame.Sequence,
                    new BuildUseIntent(buildUseRequest.PlacementTarget))
                : null,
            PlayerInteractionMode.Combat => new PlayerActionMessage(
                commandFrame.SessionId,
                commandFrame.Sequence,
                new CombatUseIntent(Player.Camera.Position, Player.Camera.Front)),
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
        Cube? placedCube = Map.TryAddCube(intent.PlacementTarget, materialId: placementMaterial);
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
        _events.Publish(new CubePlacedEvent(placedCube, placedCell));

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

    private void SubscribeReplicationExports()
    {
        _subscriptions.Add(_events.Subscribe<CubePlacedEvent>(evt =>
        {
            _pendingWorldMutations.Add(new WorldMutationDelta(
                WorldMutationKind.CubePlaced,
                evt.CellPosition));
        }));

        _subscriptions.Add(_events.Subscribe<CubeDestroyedEvent>(evt =>
        {
            _pendingWorldMutations.Add(new WorldMutationDelta(
                WorldMutationKind.CubeDestroyed,
                evt.CellPosition));
        }));

        _subscriptions.Add(_events.Subscribe<CubeFracturedEvent>(evt =>
        {
            _pendingWorldMutations.Add(new WorldMutationDelta(
                WorldMutationKind.CubeFractured,
                evt.CellPosition));
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

        PersistedPlayerProfile profile = _playerProfiles.GetOrCreate(player.DisplayName);
        _playerProfiles.Save(profile with
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
            _worldPersistence?.SaveItemInstance(item);
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
        _worldPersistence?.SaveInventorySlot(new PersistedInventorySlot(
            playerName,
            slotIndex,
            itemInstanceId,
            DateTime.UtcNow));
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
            Cube? cube = Map.TryAddCube(
                persistedCube.CellPosition,
                materialId: persistedCube.MaterialId);
            if (cube == null)
            {
                continue;
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
            anchorHeightOffset: WolfAssetDefinition.CombatAnchorHeight);
        combatant.Health.SetAuthoritativeState(enemy.Stats.CurrentHealth, enemy.Stats.MaxHealth);

        EnemyActor actor = new(
            combatant,
            enemy.Position,
            aggroRange: 10f,
            attackRange: 1.35f,
            moveSpeed: Math.Max(0f, enemy.Stats.MovementSpeed),
            preferredStandOffDistance: WolfAssetDefinition.PreferredStandOffDistance,
            collisionRadius: WolfAssetDefinition.CollisionRadius,
            collisionHeight: WolfAssetDefinition.CollisionHeight,
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

        PersistedPlayerProfile existing = _playerProfiles.GetOrCreate(player.DisplayName);
        _playerProfiles.Save(existing with
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

        _worldPersistence.DeleteCubeFracturePieces(record.Actor.Id);
        _worldPersistence.SavePlacedCube(new PersistedPlacedCube(
            record.Actor.Id,
            record.OwnerPlayerName ?? "unknown-player",
            ToCellPosition(cube.Position),
            GeometryShapeKind.SolidCube,
            materialId,
            record.Health.CurrentHealth,
            record.Health.MaxHealth,
            DateTime.UtcNow,
            DateTime.UtcNow));
    }

    private void DeletePersistedPlacedCube(int cubeId)
    {
        _worldPersistence?.DeletePlacedCube(cubeId);
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

        string ownerPlayerName = record.OwnerPlayerName ?? "unknown-player";
        _persistedFractureGroups[cell] = new PersistedFractureGroup(record.Actor.Id, ownerPlayerName);
        PersistFracturePiecesForCell(cell);
    }

    private void PersistFracturePieceCheckpoint()
    {
        if (_worldPersistence == null)
        {
            return;
        }

        foreach (GeometryCellKey cell in _persistedFractureGroups.Keys.ToArray())
        {
            PersistFracturePiecesForCell(cell);
        }
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
            _worldPersistence.SaveCubeFracturePiece(new PersistedCubeFracturePiece(
                SourceCubeId: group.SourceCubeId,
                PieceIndex: PieceIndex,
                OwnerPlayerName: group.OwnerPlayerName,
                ParentCell: new Vector3(PieceCell.X, PieceCell.Y, PieceCell.Z),
                Center: Piece.Center,
                SideLength: Piece.SideLength,
                MaterialId: MaterialId,
                CreatedUtc: DateTime.UtcNow,
                UpdatedUtc: DateTime.UtcNow));
        }
    }

    private void PersistEnemy(EnemyActor enemy)
    {
        if (_worldPersistence == null)
        {
            return;
        }

        _worldPersistence.SaveEnemyInstance(new PersistedEnemyInstance(
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
            DateTime.UtcNow,
            DateTime.UtcNow));
    }

    private void DeletePersistedEnemy(int enemyId)
    {
        _worldPersistence?.DeleteEnemyInstance(enemyId);
    }

    private readonly record struct PersistedFractureGroup(
        int SourceCubeId,
        string OwnerPlayerName);

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
            !primary.IsLocal)
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

    private IEnumerable<IAuthoritativePlayerTarget> EnumerateAuthoritativePlayerTargets()
    {
        yield return CreatePrimaryAuthoritativePlayerTarget(ResolvePrimaryControlledSessionId());

        foreach ((PlayerSessionId sessionId, RemoteSessionPlayerState actor) in _secondaryRemotePlayers)
        {
            yield return CreateSecondaryAuthoritativePlayerTarget(sessionId, actor);
        }
    }

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

    private static AuthoritativePlayerTarget CreateSecondaryAuthoritativePlayerTarget(
        PlayerSessionId sessionId,
        RemoteSessionPlayerState actor)
    {
        return new AuthoritativePlayerTarget(
            sessionId,
            actor.ActorId,
            () => actor.Position,
            () => actor.Facing,
            actor.Combatant,
            () => actor.InteractionMode,
            actor.SetWorldPosition,
            () => actor.EyePosition);
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

        if (player.ActorId == Player.Combatant.Actor.Id && IsPrimaryControlledSession(sessionId))
        {
            playerTarget = CreatePrimaryAuthoritativePlayerTarget(sessionId);
            return true;
        }

        if (_secondaryRemotePlayers.TryGetValue(sessionId, out RemoteSessionPlayerState? actor))
        {
            playerTarget = CreateSecondaryAuthoritativePlayerTarget(sessionId, actor);
            return true;
        }

        return false;
    }
}
