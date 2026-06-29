using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector3 = OpenTK.Mathematics.Vector3;

// PlayScene is currently the desktop gameplay orchestration root.
// Authoritative world/player/physics ownership is expected to move out of this scene into
// a dedicated runtime, while client presentation concerns remain scene-owned.
public class PlayScene : IDisposable
{
    // Client-local presentation state that should remain scene-owned after the runtime split.
    private readonly Render _render;
    private readonly GameSettings _settings;
    private readonly PlayerViewModelController _viewModelController;
    private readonly SceneEventHub _events = new();
    private readonly CombatFeedbackSystem _combatFeedback;

    // These fields are the current authoritative access points that a future runtime will own:
    // player lifetime, world lifetime, physics stepping, and Bullet-to-world state sync.
    private readonly Player _player;
    private Map _map;
    private readonly PhysicsIntegrator _physics;
    private readonly PhysicsSyncSystem _physicsSync = new();
    private readonly WorldObjectRegistry _worldObjects = new();
    private readonly EnemyRegistry _enemies = new();
    private PlayerInteractionMode _interactionMode = PlayerInteractionMode.Building;
    private bool _wasSettingsTogglePressed;

    public CursorState DesiredCursorState => _settings.CurrentRuntimeState.DesiredCursorState;

    public Vector3 LastReportedPlayerPosition => _player.LastReportedPosition;

    internal SceneEventHub Events => _events;

    public PlayScene(GameSettings settings)
    {
        _settings = settings;
        // Desktop startup still constructs the authoritative world stack locally.
        // Phase 0 keeps that behavior intact and only marks the extraction seam.
        _physics = new PhysicsIntegrator();
        _map = new Map(_physics, width: _settings.FloorSize, depth: _settings.FloorSize);
        _viewModelController = new PlayerViewModelController(ViewModelCatalog.CreateDefault(), _settings);

        Vector3 playerStartPosition = new Vector3(5, 1.5f, 5);
        PhysicsFactory physicsFactory = new();
        var playerBody = physicsFactory.CreatePlayerBody(playerStartPosition.ToBullet());
        _physics.RegisterPlayerBody(playerBody, PhysicsBodyOwnership.IntegratorOwned);
        _player = new Player(_map, _physics, playerBody, playerStartPosition, _events);
        _render = new Render(_settings);
        _combatFeedback = new CombatFeedbackSystem(_events, new FloatingCombatTextSystem());
        SpawnInitialWolf();
    }

    public void Update(FrameEventArgs args, KeyboardState keyboardState, MouseState mouseState)
    {
        float delta = (float)args.Time;

        // Viewmodel pose refresh is presentation-local and should remain scene/client owned.
        _viewModelController.RefreshPose();
        _player.Combatant.AdvanceCombat(delta);
        HandleGlobalInput(keyboardState);
        bool allowGameplayInput = _settings.CurrentRuntimeState.AllowGameplayInput;
        PlayerInteractionCommand interaction = _player.CaptureInteractionCommand(keyboardState, mouseState, allowGameplayInput);
        ApplyInteractionModeSelection(interaction);
        bool primaryUseTriggered = _player.ConsumePrimaryUsePressed(
            interaction,
            _player.Combatant.DerivedStats.AttackIntervalSeconds,
            delta);
        if (primaryUseTriggered)
        {
            _viewModelController.TriggerPrimaryUse();
            if (_interactionMode == PlayerInteractionMode.Combat)
            {
                HandleCombatPrimaryUse();
            }
        }

        ViewModelUpdateResult viewModelUpdateResult =
            _render.ViewModels.Update(_viewModelController.CurrentFrameState, delta);

        if (viewModelUpdateResult.HandsFinished)
        {
            _viewModelController.NotifyAnimationFinished(ViewModelSlot.Hands);
        }

        if (viewModelUpdateResult.HeldItemFinished)
        {
            _viewModelController.NotifyAnimationFinished(ViewModelSlot.HeldItem);
        }

        // This block is the current authoritative simulation seam:
        // physics step, player state update, local world mutation, feedback tick, and transform sync.
        // The future runtime should own this order while PlayScene remains the client orchestrator.
        _physics.StepSimulation(delta);
        _player.Update(delta, keyboardState, mouseState, _settings, allowGameplayInput);
        if (allowGameplayInput)
        {
            _enemies.UpdateAll(delta, _player.GetWorldPosition(), _player.Combatant, _events, _map);
            _enemies.PruneInactive();
            EnsureWolfExists();
            ResolvePlayerEnemySeparation();
        }

        if (_interactionMode == PlayerInteractionMode.Building)
        {
            Cube? placedCube = null;
            if (_player.TryCreateBuildUseRequest(primaryUseTriggered, out BuildUseRequest buildUseRequest))
            {
                placedCube = _map.TryAddCube(buildUseRequest.PlacementTarget);
            }

            if (placedCube != null && placedCube.Position.Y > 0f)
            {
                _worldObjects.RegisterCube(placedCube, _map);
                _events.Publish(new CubePlacedEvent(placedCube));
            }
        }

        _combatFeedback.Update(delta);
        _physicsSync.SyncDynamicMapBodies(_physics.DynamicMapCubeBodies);
    }

    public void Render(FrameEventArgs args, bool windowFocused, MouseState mouseState, Action<Vector2i> applyClientSize)
    {
        // Render primarily consumes scene/runtime state for desktop presentation, even though
        // this path still applies scene-owned runtime requests such as floor rebuilds today.
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _render.World.DrawWorld(
            _player.Camera,
            _map.GetRenderCandidateCubes(_player.Camera.Position, WorldVisibility.MaxRenderDistance));
        _render.Enemies.Draw(
            _player.Camera,
            _enemies.ActiveActors,
            (float)args.Time,
            _settings.ShowCollisionGeometry);

        if (_settings.ShowCollisionGeometry)
        {
            _render.Debug.DrawCollisionBodies(_physics.MapCubeBodies, _player.Camera);
            _render.DrawCollisionBody(_player.PhysicsBody, _player.Camera);
        }

        _render.ViewModels.Draw(_viewModelController.CurrentFrameState);
        PlayerCombatDerivedStats derivedStats = _player.Combatant.DerivedStats;
        PlayerCombatStats playerStats = _player.Combatant.Stats;
        HudStatusSnapshot hudStatus = new(
            _player.Combatant.Health.CurrentHealth,
            _player.Combatant.Health.MaxHealth,
            _interactionMode,
            derivedStats.MinDamage,
            derivedStats.MaxDamage,
            playerStats.Strength,
            playerStats.AttackSpeed,
            playerStats.Vitality);

        HudFrameResult hudResult =
            _render.Hud.DrawFrame(args, windowFocused, _settings, mouseState, _player.Camera, _combatFeedback, hudStatus);
        ApplyHudRequest(hudResult.Request, applyClientSize);
        _render.Hud.UpdateProjectionForSettings(_settings);
    }

    public void Resize(Vector2i size)
    {
        GL.Viewport(0, 0, size.X, size.Y);
        _settings.ApplyResolution(size);
        _render.Hud.UpdateProjectionForSettings(_settings);
    }

    public void Dispose()
    {
        _combatFeedback.Dispose();
        _physics.Dispose();
    }

    private void HandleGlobalInput(KeyboardState keyboardState)
    {
        bool settingsTogglePressed = keyboardState.IsKeyDown(Keys.Escape);
        if (settingsTogglePressed && !_wasSettingsTogglePressed)
        {
            _settings.ToggleSettings();
        }

        _wasSettingsTogglePressed = settingsTogglePressed;
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

    private void HandleCombatPrimaryUse()
    {
        EnemyActor? enemy = CombatUseResolver.TryResolveEnemyHit(
            _enemies,
            _player.Camera.Position,
            _player.Camera.Front,
            CombatUseResolver.DefaultHitRange);
        if (enemy != null)
        {
            PlayerAttackResolution attack = _player.Combatant.ResolveAttack(
                () => Random.Shared.Next(
                    _player.Combatant.DerivedStats.MinDamage,
                    _player.Combatant.DerivedStats.MaxDamage + 1),
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

            return;
        }

        WorldObjectRecord? record = CombatUseResolver.TryResolveDestructibleCubeHit(
            _map,
            _worldObjects,
            _player.Camera.Position,
            _player.Camera.Front,
            CombatUseResolver.DefaultHitRange);
        if (record != null)
        {
            PlayerAttackResolution attack = _player.Combatant.ResolveAttack(
                () => Random.Shared.Next(
                    _player.Combatant.DerivedStats.MinDamage,
                    _player.Combatant.DerivedStats.MaxDamage + 1),
                () => Random.Shared.NextDouble());
            DamageReceipt receipt = record.ApplyDamage(attack.Damage);
            if (receipt.AppliedAmount > 0)
            {
                _events.Publish(new DamageAppliedEvent(
                    record.Actor,
                    receipt.AppliedAmount,
                    attack.FloatingTextKind));
            }

            if (receipt.WasFatal)
            {
                _events.Publish(new ActorDiedEvent(record.Actor));
                object sourceObject = record.SourceObject;
                bool removed = _worldObjects.TryRemove(record);
                if (removed && sourceObject is Cube destroyedCube)
                {
                    _events.Publish(new CubeDestroyedEvent(destroyedCube));
                }
            }
        }
    }

    private void ApplyHudRequest(HudInteractionRequest request, Action<Vector2i> applyClientSize)
    {
        if (request.FieldOfViewDegrees.HasValue)
        {
            _settings.FieldOfViewDegrees = request.FieldOfViewDegrees.Value;
        }

        if (request.MouseSensitivity.HasValue)
        {
            _settings.MouseSensitivity = request.MouseSensitivity.Value;
        }

        if (request.NoclipSpeed.HasValue)
        {
            _settings.NoclipSpeed = request.NoclipSpeed.Value;
        }

        if (request.StagedFloorSize.HasValue)
        {
            _settings.StageFloorSize(request.StagedFloorSize.Value);
        }

        if (request.ShowCollisionGeometry.HasValue)
        {
            _settings.SetShowCollisionGeometry(request.ShowCollisionGeometry.Value);
        }

        if (request.Strength.HasValue || request.AttackSpeed.HasValue || request.Vitality.HasValue)
        {
            PlayerCombatStats updatedStats = new()
            {
                Strength = request.Strength ?? _player.Combatant.Stats.Strength,
                AttackSpeed = request.AttackSpeed ?? _player.Combatant.Stats.AttackSpeed,
                Vitality = request.Vitality ?? _player.Combatant.Stats.Vitality
            };
            _player.Combatant.ApplyStats(updatedStats);
        }

        if (request.FloatingCombatText.HasValue)
        {
            _settings.ApplyFloatingCombatTextTuning(request.FloatingCombatText.Value);
        }

        if (request.RequestedResolution.HasValue)
        {
            applyClientSize(request.RequestedResolution.Value);
            _settings.ApplyResolution(request.RequestedResolution.Value);
        }

        if (request.RequestedFloorSize.HasValue && request.RequestedFloorSize.Value != _settings.FloorSize)
        {
            _settings.StageFloorSize(request.RequestedFloorSize.Value);
            _settings.ApplyPendingFloorSize();
            RebuildMapForFloorSize(_settings.FloorSize);
        }
    }

    private void RebuildMapForFloorSize(int floorSize)
    {
        // Floor rebuild is a current scene-owned authoritative world mutation path.
        // The future runtime should own map rebuilds plus player re-clamping, even if the request
        // still originates from client UI.
        Vector3 currentPlayerPosition = _player.GetWorldPosition();
        Map previousMap = _map;
        WorldObjectCubeSnapshot worldObjectSnapshot = _worldObjects.CreateCubeSnapshot();

        _physics.ResetMapBodies();

        var rebuiltMap = new Map(_physics, width: floorSize, depth: floorSize);
        previousMap.CopyPlacedCubesTo(rebuiltMap);
        _worldObjects.RestoreCubeSnapshot(rebuiltMap, worldObjectSnapshot);

        _map = rebuiltMap;
        _player.BindMap(rebuiltMap);
        _enemies.ClampToMapBounds(rebuiltMap);

        Vector3 clampedPosition = new Vector3(
            Math.Clamp(currentPlayerPosition.X, 0.5f, rebuiltMap.Width - 0.5f),
            Math.Clamp(currentPlayerPosition.Y, 1.5f, rebuiltMap.Height - 0.5f),
            Math.Clamp(currentPlayerPosition.Z, 0.5f, rebuiltMap.Depth - 0.5f));

        _player.SetWorldPosition(clampedPosition);
    }

    private void SpawnInitialWolf()
    {
        Vector3 spawnPosition = WolfSpawnPolicy.ResolveInitialSpawn(_map.Width, _map.Depth);

        EnemyCombatant combatant = new(
            () => spawnPosition,
            _events,
            actorId: 2000,
            maxHealth: 90,
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
            collisionHeight: WolfAssetDefinition.CollisionHeight);
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

    private void ResolvePlayerEnemySeparation()
    {
        if (_player.IsNoclipEnabled)
        {
            return;
        }

        Vector3 playerPosition = _player.GetWorldPosition();
        Vector3 resolvedPosition = playerPosition;
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

        if ((resolvedPosition - playerPosition).LengthSquared > 0.000001f)
        {
            _player.SetWorldPosition(resolvedPosition);
        }
    }
}
