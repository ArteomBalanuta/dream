using OpenTK.Mathematics;

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
    private readonly bool _loadVisualAssets;
    private readonly SceneEventHub _events;
    private readonly PhysicsSyncSystem _physicsSync = new();
    private readonly WorldObjectRegistry _worldObjects;
    private readonly EnemyRegistry _enemies;
    private readonly PlayerSessionRegistry _sessions = new();
    private readonly Dictionary<PlayerSessionId, RemoteSessionPlayerState> _secondaryRemotePlayers = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<WorldMutationDelta> _pendingWorldMutations = [];
    private readonly List<DamageEventDelta> _pendingDamageEvents = [];
    private PlayerInteractionMode _interactionMode = PlayerInteractionMode.Building;
    private PlayerCommandFrame _lastAppliedCommandFrame;
    private int _nextRemoteActorId = 2;

    public WorldSessionRuntime(
        int floorSize,
        Vector3 playerStartPosition,
        SceneEventHub events,
        bool loadVisualAssets = true)
    {
        _loadVisualAssets = loadVisualAssets;
        _events = events;
        Physics = new PhysicsIntegrator();
        Map = new Map(Physics, loadVisualAssets: _loadVisualAssets, width: floorSize, depth: floorSize);

        PhysicsFactory physicsFactory = new();
        var playerBody = physicsFactory.CreatePlayerBody(playerStartPosition.ToBullet());
        Physics.RegisterPlayerBody(playerBody, PhysicsBodyOwnership.IntegratorOwned);
        Player = new Player(Map, Physics, playerBody, playerStartPosition, _events);
        LocalPlayerSessionId = PlayerSessionId.LocalDefault;
        _sessions.Register(
            LocalPlayerSessionId,
            Player.Combatant.Actor.Id,
            isLocal: true,
            displayName: "local-player");
        _lastAppliedCommandFrame = CreateIdleCommandFrame(LocalPlayerSessionId, sequence: 0);

        _worldObjects = new WorldObjectRegistry();
        _enemies = new EnemyRegistry();
        SubscribeReplicationExports();

        SpawnInitialWolf();
    }

    public Map Map { get; private set; }

    public PhysicsIntegrator Physics { get; }

    public Player Player { get; }

    public EnemyRegistry Enemies => _enemies;

    public PlayerInteractionMode InteractionMode => _interactionMode;

    public void SetAuthoritativeInteractionMode(PlayerInteractionMode interactionMode)
    {
        _interactionMode = interactionMode;
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
            PrimaryRemoteSessionId = primary.SessionId;
            return primary;
        }

        int actorId = AllocateRemoteActorId();
        Vector3 spawnPosition = ResolveSecondaryRemoteSpawnPosition(_secondaryRemotePlayers.Count);
        _secondaryRemotePlayers[sessionId] = new RemoteSessionPlayerState(actorId, sessionId, spawnPosition);
        return _sessions.Register(
            sessionId,
            actorId,
            isLocal: false,
            displayName: displayName);
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
        ApplyInteractionModeSelection(interaction);
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

        Physics.StepSimulation(delta);
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

        _physicsSync.SyncDynamicMapBodies(Physics.DynamicMapCubeBodies);
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
        return WorldSessionPresentationState.Create(
            presentationCamera,
            Map.GetRenderCandidateCubes(presentationCamera.Position, maxRenderDistance),
            _enemies.ActiveActors,
            Physics.MapCubeBodies,
            Player.PhysicsBody,
            Player.Combatant.Health,
            _interactionMode,
            Player.Combatant.DerivedStats,
            Player.Combatant.Stats);
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

        var rebuiltMap = new Map(Physics, loadVisualAssets: _loadVisualAssets, width: floorSize, depth: floorSize);
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
                PlayerCombatStatsSnapshot.From(playerTarget.Combatant.Stats),
                displayName));
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
                actor.DisplayName));
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
            ? new WorldSnapshot(
                Map.Width,
                Map.Height,
                Map.Depth,
                Map.SolidCubes
                    .Where(cube => MathF.Floor(cube.Position.Y + 1e-6f) > 0f)
                    .Select(cube => new Vector3(
                        MathF.Floor(cube.Position.X + 1e-6f),
                        MathF.Floor(cube.Position.Y + 1e-6f),
                        MathF.Floor(cube.Position.Z + 1e-6f)))
                    .ToArray())
            : null;

        ServerSnapshotEnvelope envelope = new(
            CurrentServerTick,
            acceptedSequences,
            actors,
            _interactionMode,
            worldSnapshot,
            _pendingWorldMutations.ToArray(),
            _pendingDamageEvents.ToArray());

        _pendingWorldMutations.Clear();
        _pendingDamageEvents.Clear();
        return envelope;
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

        return message.Payload switch
        {
            BuildUseIntent buildIntent when context.SupportsBuildActions
                => ProcessBuildUseAction(message, buildIntent, context.InteractionMode),
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

        actor.AdvanceCombat(deltaTime);
        actor.ApplyCommand(commandFrame, playerTuning, deltaTime);
        actor.ClampToMap(Map);
    }

    private void ApplyInteractionModeSelection(PlayerInteractionCommand command)
    {
        if (command.SelectBuildingModePressed)
        {
            _interactionMode = PlayerInteractionMode.Building;
        }
        else if (command.SelectCombatModePressed)
        {
            _interactionMode = PlayerInteractionMode.Combat;
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
        bool removed = _worldObjects.TryRemove(record);
        if (removed && sourceObject is Cube destroyedCube)
        {
            _events.Publish(new CubeDestroyedEvent(destroyedCube));
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
            displayName: "Wolf");
        _enemies.Add(wolf);
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
        return _interactionMode switch
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
        PlayerInteractionMode interactionMode)
    {
        if (interactionMode != PlayerInteractionMode.Building)
        {
            return CreateRejectedActionResult(message, "Build action rejected because the authoritative mode is not Building.");
        }

        Cube? placedCube = Map.TryAddCube(intent.PlacementTarget);
        if (placedCube == null || placedCube.Position.Y <= 0f)
        {
            return CreateRejectedActionResult(message, "Build placement rejected by authoritative validation.");
        }

        _worldObjects.RegisterCube(placedCube, Map);
        _events.Publish(new CubePlacedEvent(placedCube));

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new WorldMutationResult(
                WorldMutationKind.CubePlaced,
                ToCellPosition(placedCube.Position),
                Applied: true));
    }

    private AuthoritativeActionResult ProcessPlayerStatsUpdateAction(
        PlayerActionMessage message,
        PlayerCombatant combatant,
        PlayerStatsUpdateIntent intent)
    {
        combatant.ApplyStats(intent.Stats.ToCombatStats());

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new PlayerStatsUpdateResult(PlayerCombatStatsSnapshot.From(combatant.Stats)));
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
                ToCellPosition(evt.Cube.Position)));
        }));

        _subscriptions.Add(_events.Subscribe<CubeDestroyedEvent>(evt =>
        {
            _pendingWorldMutations.Add(new WorldMutationDelta(
                WorldMutationKind.CubeDestroyed,
                ToCellPosition(evt.Cube.Position)));
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
            () => _interactionMode,
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
