using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Application;
using Dream.Client.Input;
using Dream.Client.Networking;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Configuration;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Core;
using Dream.Rendering.Hud;
using Dream.Rendering.ViewModels;
using Dream.Rendering.World;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Client.Scenes;


// PlayScene is currently the desktop gameplay orchestration root.
// Authoritative world/player/physics ownership is expected to move out of this scene into
// a dedicated runtime, while client presentation concerns remain scene-owned.
public class PlayScene : IDisposable
{
    private const string LoopbackRemotePlayersEnvironmentVariable = "DREAM_LOOPBACK_REMOTE_PLAYERS";
    private const string RenderPerformanceLogEnvironmentVariable = "DREAM_RENDER_PERF_LOG";
    private const double RemotePlayerInterpolationDelaySeconds = 1d / 10d;

    // Client-local presentation state that should remain scene-owned after the runtime split.
    private readonly Render _render;
    private readonly GameSettings _settings;
    private readonly DesktopClientConfig? _desktopClientConfig;
    private readonly Action? _persistDesktopClientConfig;
    private readonly PlayerViewModelController _viewModelController;
    private readonly SceneEventHub _events = new();
    private readonly CombatFeedbackSystem _combatFeedback;
    private readonly AuthoritativeActionFeedbackSystem _actionFeedback;
    private readonly MultiplayerDiagnostics _diagnostics;
    private readonly ClientProtocolSession _clientProtocol;
    private readonly ClientScenePresenter _scenePresenter;
    private readonly RemoteClientSession? _remoteClient;
    private readonly GameplayInputResumeFilter _gameplayInputResumeFilter = new();
    private readonly PlayerCommandOneShotInputFilter _commandInputFilter = new();
    private readonly LatestPlayerCommandInbox _localSimulationCommandInbox = new();
    private readonly bool _renderPerformanceLoggingEnabled;

    // Authoritative world/player/physics ownership now flows through WorldSessionRuntime.
    // PlayScene remains the desktop composition root plus client presentation owner.
    private readonly WorldSessionRuntime _runtime;
    private readonly ServerTickRunner _tickRunner = new();
    private readonly bool _enableLoopbackRemotePlayers;
    private readonly ClientActionSequenceTracker _actionSequences = new();
    private ulong _nextLocalCommandSequence = 1;
    private PlayerCommandFrame? _lastLocalSimulationCommandFrame;
    private bool _wasSettingsTogglePressed;
    private bool _wasInventoryTogglePressed;
    private bool _wasCameraViewTogglePressed;
    private bool _localPlayerWasMoving;
    private double _clientPresentationTimeSeconds;
    private double _renderPerformanceLogSeconds;
    private PlayerInventorySnapshot? _inventory;

    public CursorState DesiredCursorState => _settings.CurrentRuntimeState.DesiredCursorState;

    public Vector3 LastReportedPlayerPosition => _runtime.Player.LastReportedPosition;

    public string? ConnectedServerEndpoint => _remoteClient?.Options.EndpointLabel;

    internal SceneEventHub Events => _events;

    public PlayScene(
        GameSettings settings,
        DesktopClientConfig? clientConfig = null,
        Action? persistDesktopClientConfig = null,
        bool enableLoopbackRemotePlayers = false,
        RemoteClientSession? remoteClient = null)
    {
        _settings = settings;
        _desktopClientConfig = clientConfig;
        _persistDesktopClientConfig = persistDesktopClientConfig;
        _enableLoopbackRemotePlayers = enableLoopbackRemotePlayers;
        _remoteClient = remoteClient;
        _renderPerformanceLoggingEnabled = IsRenderPerformanceLoggingEnabled();
        // The desktop scene now delegates authoritative world-stack creation to WorldSessionRuntime
        // while remaining the client composition root for presentation systems.
        _viewModelController = new PlayerViewModelController(ViewModelCatalog.CreateDefault(), _settings);
        _clientProtocol = ClientProtocolSession.CreateDefault();

        Vector3 playerStartPosition = new Vector3(5, 1.5f, 5);
        _runtime = new WorldSessionRuntime(_settings.FloorSize, playerStartPosition, _events);
        if (_desktopClientConfig != null)
        {
            _runtime.Player.Combatant.ApplyStats(_desktopClientConfig.ToPlayerCombatStats());
        }

        _scenePresenter = new ClientScenePresenter(_remoteClient?.SessionId ?? _runtime.LocalPlayerSessionId.Value);
        _render = new Render(_settings);
        _combatFeedback = new CombatFeedbackSystem(_events, new FloatingCombatTextSystem());
        _actionFeedback = new AuthoritativeActionFeedbackSystem(_events);
        _diagnostics = new MultiplayerDiagnostics();

        if (_remoteClient == null)
        {
            ApplyInventorySnapshot(_runtime.TryCaptureInventorySnapshot(_runtime.LocalPlayerSessionId));
        }
    }

    public void Update(FrameEventArgs args, KeyboardState keyboardState, MouseState mouseState)
    {
        float delta = (float)args.Time;
        _clientPresentationTimeSeconds += args.Time;
        DrainRemoteTransportMessages();

        // Viewmodel pose refresh is presentation-local and should remain scene/client owned.
        _viewModelController.RefreshPose();
        HandleGlobalInput(keyboardState);
        bool allowGameplayInput = _settings.CurrentRuntimeState.AllowGameplayInput;
        PlayerCommandFrame rawCommandFrame = PlayerController.CaptureCommandFrame(
            _runtime.LocalPlayerSessionId,
            _nextLocalCommandSequence++,
            keyboardState,
            mouseState,
            allowGameplayInput);
        rawCommandFrame = _gameplayInputResumeFilter.Filter(rawCommandFrame, allowGameplayInput);
        PlayerCommandFrame commandFrame = _commandInputFilter.Filter(rawCommandFrame);
        _actionSequences.ObserveCommandSequence(commandFrame.Sequence);
        _localPlayerWasMoving = IsMoving(commandFrame.MoveIntent);
        _localSimulationCommandInbox.Enqueue(commandFrame);
        ApplyImmediateLocalLook(commandFrame, allowGameplayInput);
        SendRemoteCommandFrameIfConnected(commandFrame);
        PlayerSimulationTuning tuning = PlayerSimulationTuning.FromSettings(_settings);
        bool primaryUseTriggered = false;
        _tickRunner.Advance(args.Time, (tick, fixedDeltaSeconds) =>
        {
            PlayerCommandFrame simulationCommandFrame = ResolveLocalSimulationCommandFrame(commandFrame);
            WorldSessionUpdateResult result = _runtime.AdvanceAuthoritativeTick(
                tick,
                fixedDeltaSeconds,
                simulationCommandFrame,
                tuning,
                allowGameplaySimulation: _remoteClient == null && allowGameplayInput);
            if (result.PrimaryUseTriggered)
            {
                primaryUseTriggered = true;
            }
        });

        if (primaryUseTriggered)
        {
            _viewModelController.TriggerPrimaryUse();
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

        UpdateLoopbackRemotePlayerDebugState();
        _combatFeedback.Update(delta, _settings.FloatingCombatText);
        _actionFeedback.Update(delta);
    }

    public void Render(FrameEventArgs args, bool windowFocused, MouseState mouseState, Action<Vector2i> applyClientSize)
    {
        // Render primarily consumes scene/runtime state for desktop presentation, even though
        // this path still applies scene-owned runtime requests such as floor rebuilds today.
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        WorldSessionPresentationState presentation = _runtime.CapturePresentationState(
            WorldVisibility.MaxRenderDistance,
            (float)_tickRunner.RemainingSeconds,
            _settings.CameraViewMode);
        IReadOnlyList<RemotePlayerPresentationState> remotePlayerStates = _scenePresenter.BuildRemotePlayerStates(
            Math.Max(0d, _clientPresentationTimeSeconds - RemotePlayerInterpolationDelaySeconds));

        WorldRenderStats worldRenderStats = _render.World.DrawWorld(presentation);
        FramePerformanceSnapshot performance = presentation.Performance.WithRenderStats(
            worldRenderStats.VisibleCompatibilityCubes,
            worldRenderStats.VisibleGeometryBlocks,
            worldRenderStats.CompatibilityBatchCount,
            worldRenderStats.GeometryBatchCount,
            worldRenderStats.VisibleStaticChunks,
            worldRenderStats.StaticChunkMeshCount,
            worldRenderStats.StaticChunkExposedFaces,
            worldRenderStats.StaticChunkTriangles,
            worldRenderStats.StaticChunkSignatureMilliseconds,
            worldRenderStats.StaticChunkExtractMilliseconds,
            worldRenderStats.StaticChunkPlanMilliseconds,
            worldRenderStats.StaticChunkSyncMilliseconds,
            worldRenderStats.StaticChunkDrawMilliseconds,
            worldRenderStats.StaticChunkRebuiltMeshes,
            worldRenderStats.StaticChunkReusedMeshes);
        LogRenderPerformanceIfEnabled(args, performance);
        _render.Enemies.Draw(
            presentation.Camera,
            presentation.ActiveEnemies,
            (float)args.Time,
            _settings.ShowCollisionGeometry);
        if (_settings.CameraViewMode == CameraViewMode.ThirdPerson)
        {
            _render.LocalPlayers.Draw(
                new LocalPlayerPresentationState(
                    _runtime.Player.GetWorldPosition(),
                    _runtime.Player.Camera.HorizontalForward,
                    _localPlayerWasMoving),
                presentation.Camera,
                (float)args.Time);
        }

        _render.RemotePlayers.Draw(
            presentation.Camera,
            remotePlayerStates,
            (float)args.Time);

        if (_settings.ShowCollisionGeometry)
        {
            _render.Debug.DrawCollisionBodies(presentation.MapCollisionBodies, presentation.Camera);
            if (presentation.PlayerCollisionBody != null)
            {
                _render.DrawCollisionBody(presentation.PlayerCollisionBody, presentation.Camera);
            }
        }

        if (_settings.CameraViewMode == CameraViewMode.FirstPerson)
        {
            _render.ViewModels.Draw(_viewModelController.CurrentFrameState);
        }

        HudFrameResult hudResult =
            _render.Hud.DrawFrame(
                args,
                windowFocused,
                _settings,
                mouseState,
                presentation,
                remotePlayerStates,
                _scenePresenter.BuildPlayerRoster(),
                _combatFeedback,
                _actionFeedback.CurrentRejectionMessage,
                _inventory,
                ConnectedServerEndpoint,
                performance);
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
        _actionFeedback.Dispose();
        _runtime.Dispose();
        if (_remoteClient != null)
        {
            _remoteClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public void ApplyReplicatedMessage(IServerToClientMessage message)
    {
        _clientProtocol.ApplyServerMessage(message);
        if (message is JoinAcceptedMessage joinAcceptedMessage && _remoteClient != null)
        {
            ClientJoinAcceptedApplier.ApplyToRuntime(_runtime, joinAcceptedMessage);
            ClientJoinAcceptedApplier.ApplyToPresenter(_scenePresenter, joinAcceptedMessage);
            ApplyInventorySnapshot(joinAcceptedMessage.Inventory);
        }
        else if (message is ServerSnapshotMessage snapshotMessage)
        {
            if (_remoteClient != null)
            {
                RemoteWorldMutationApplier.ApplySnapshotToRuntime(_runtime, snapshotMessage.Snapshot);
                ClientCorrectionApplier.ApplySnapshotToRuntime(
                    _runtime,
                    snapshotMessage.Snapshot,
                    _remoteClient.SessionId);
                RemoteEnemyStateApplier.ApplyActorSnapshots(_runtime.Enemies, snapshotMessage.Snapshot.Actors);
                ClientSnapshotDamageApplier.ApplyToRuntime(_runtime, _events, snapshotMessage.Snapshot);
                _diagnostics.RecordSnapshotApplied(snapshotMessage.Snapshot, _remoteClient.SessionId);
            }

            _scenePresenter.ApplySnapshot(snapshotMessage.Snapshot, _clientPresentationTimeSeconds);
        }
        else if (message is ServerActionMessage actionMessage && _remoteClient != null)
        {
            _actionSequences.ObserveActionResult(actionMessage.Result);
            ClientAuthoritativeActionApplier.ApplyActionResult(_runtime, _events, actionMessage.Result, _diagnostics);
            if (actionMessage.Result.Payload is InventoryChangedResult inventoryChanged)
            {
                ApplyInventorySnapshot(inventoryChanged.Inventory);
            }
        }
    }

    private void UpdateLoopbackRemotePlayerDebugState()
    {
        if (!_enableLoopbackRemotePlayers)
        {
            return;
        }

        Vector3 localPosition = _runtime.Player.GetWorldPosition();
        float orbit = (float)_clientPresentationTimeSeconds;
        Vector3 remoteOnePosition = localPosition + new Vector3(MathF.Cos(orbit) * 3f, 0f, -5f + MathF.Sin(orbit) * 2f);
        Vector3 remoteTwoPosition = localPosition + new Vector3(-4f, 0f, -6f + MathF.Cos(orbit * 0.5f) * 1.5f);

        ServerSnapshotEnvelope snapshot = new(
            _runtime.CurrentServerTick,
            new Dictionary<string, ulong>(),
            [
                new ActorSnapshot(
                    _runtime.Player.Combatant.Actor.Id,
                    CreatureKind.Player,
                    _runtime.LocalPlayerSessionId.Value,
                    localPosition,
                    _runtime.Player.Camera.Front,
                    _runtime.Player.Combatant.Health.CurrentHealth,
                    _runtime.Player.Combatant.Health.MaxHealth,
                    _runtime.Player.Combatant.Health.IsAlive,
                    null,
                    null,
                    "local-player"),
                new ActorSnapshot(
                    9001,
                    CreatureKind.Player,
                    "loopback-remote-1",
                    remoteOnePosition,
                    (-Vector3.UnitZ).Normalized(),
                    100,
                    100,
                    true,
                    null,
                    null,
                    "Loopback One"),
                new ActorSnapshot(
                    9002,
                    CreatureKind.Player,
                    "loopback-remote-2",
                    remoteTwoPosition,
                    (-Vector3.UnitZ).Normalized(),
                    100,
                    100,
                    true,
                    null,
                    null,
                    "Loopback Two")
            ],
            _runtime.InteractionMode,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        ApplyReplicatedMessage(new ServerSnapshotMessage(snapshot));
    }

    public static bool ResolveLoopbackRemotePlayersEnabled()
    {
        string? value = Environment.GetEnvironmentVariable(LoopbackRemotePlayersEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRenderPerformanceLoggingEnabled()
    {
        string? value = Environment.GetEnvironmentVariable(RenderPerformanceLogEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private void LogRenderPerformanceIfEnabled(FrameEventArgs args, FramePerformanceSnapshot performance)
    {
        if (!_renderPerformanceLoggingEnabled || !performance.HasCounters)
        {
            return;
        }

        _renderPerformanceLogSeconds += args.Time;
        if (_renderPerformanceLogSeconds < 1d)
        {
            return;
        }

        _renderPerformanceLogSeconds = 0d;
        Console.WriteLine(
            $"[render-perf] frame={args.Time * 1000d:0.00}ms pos={LastReportedPlayerPosition} " +
            string.Join(" | ", performance.ToHudLines()));
    }

    private void HandleGlobalInput(KeyboardState keyboardState)
    {
        bool settingsTogglePressed = keyboardState.IsKeyDown(Keys.Escape);
        if (settingsTogglePressed && !_wasSettingsTogglePressed)
        {
            _settings.ToggleSettings();
        }

        _wasSettingsTogglePressed = settingsTogglePressed;

        bool inventoryTogglePressed = keyboardState.IsKeyDown(Keys.V);
        if (inventoryTogglePressed &&
            !_wasInventoryTogglePressed &&
            !_settings.SettingsVisible)
        {
            _settings.ToggleInventory();
        }

        _wasInventoryTogglePressed = inventoryTogglePressed;

        bool cameraViewTogglePressed = keyboardState.IsKeyDown(Keys.C);
        if (cameraViewTogglePressed &&
            !_wasCameraViewTogglePressed &&
            _settings.CurrentRuntimeState.AllowGameplayInput)
        {
            _settings.ToggleCameraViewMode();
            PersistDesktopClientConfig();
        }

        _wasCameraViewTogglePressed = cameraViewTogglePressed;
    }

    private void DrainRemoteTransportMessages()
    {
        if (_remoteClient == null)
        {
            return;
        }

        while (_remoteClient.TryDequeueInbound(out IServerToClientMessage? message) && message != null)
        {
            ApplyReplicatedMessage(message);
        }
    }

    private void SendRemoteCommandFrameIfConnected(PlayerCommandFrame localCommandFrame)
    {
        if (_remoteClient == null || !_remoteClient.IsConnected)
        {
            return;
        }

        PlayerCommandFrame remoteCommandFrame = RemotePlayerCommandRelay.CreateRelayCommand(
            localCommandFrame,
            new PlayerSessionId(_remoteClient.SessionId),
            _runtime.Player.Camera.Front);
        _remoteClient.SendCommandFrameAsync(remoteCommandFrame).AsTask().GetAwaiter().GetResult();
    }

    private void ApplyImmediateLocalLook(PlayerCommandFrame commandFrame, bool allowGameplayInput)
    {
        if (!allowGameplayInput)
        {
            return;
        }

        _runtime.Player.ApplyImmediateLook(
            new Vector2(commandFrame.LookIntent.YawDelta, commandFrame.LookIntent.PitchDelta),
            _settings.MouseSensitivity,
            allowLook: true);
    }

    private PlayerCommandFrame ResolveLocalSimulationCommandFrame(PlayerCommandFrame currentFrame)
    {
        IReadOnlyList<PlayerCommandFrame> pendingFrames =
            _localSimulationCommandInbox.Drain([currentFrame.SessionId]);
        PlayerCommandFrame simulationSource = pendingFrames.Count > 0
            ? pendingFrames[^1]
            : _lastLocalSimulationCommandFrame ?? ClearOneShotMoveInputs(currentFrame);

        _lastLocalSimulationCommandFrame = ClearOneShotMoveInputs(simulationSource);
        return ClientSimulationCommandPolicy.PrepareForLocalSimulation(
            simulationSource,
            useRemoteAuthority: _remoteClient != null);
    }

    private static PlayerCommandFrame ClearOneShotMoveInputs(PlayerCommandFrame frame)
    {
        PlayerMoveIntent move = frame.MoveIntent;
        return frame with
        {
            MoveIntent = new PlayerMoveIntent(
                move.ForwardAxis,
                move.StrafeAxis,
                move.VerticalAxis,
                JumpPressed: false,
                FreezePressed: false,
                ToggleNoclipPressed: false)
        };
    }

    private void ApplyHudRequest(HudInteractionRequest request, Action<Vector2i> applyClientSize)
    {
        bool persistDesktopConfig = false;

        if (request.FieldOfViewDegrees.HasValue)
        {
            _settings.FieldOfViewDegrees = request.FieldOfViewDegrees.Value;
            persistDesktopConfig = true;
        }

        if (request.MouseSensitivity.HasValue)
        {
            _settings.MouseSensitivity = request.MouseSensitivity.Value;
            persistDesktopConfig = true;
        }

        if (request.NoclipSpeed.HasValue)
        {
            _settings.NoclipSpeed = request.NoclipSpeed.Value;
            persistDesktopConfig = true;
        }

        if (request.CameraViewMode.HasValue)
        {
            _settings.SetCameraViewMode(request.CameraViewMode.Value);
            persistDesktopConfig = true;
        }

        if (request.StagedFloorSize.HasValue)
        {
            _settings.StageFloorSize(request.StagedFloorSize.Value);
        }

        if (request.ShowCollisionGeometry.HasValue)
        {
            _settings.SetShowCollisionGeometry(request.ShowCollisionGeometry.Value);
        }

        if (request.ShowPerformanceCounters.HasValue)
        {
            _settings.SetShowPerformanceCounters(request.ShowPerformanceCounters.Value);
        }

        if (request.Strength.HasValue || request.AttackSpeed.HasValue || request.Vitality.HasValue)
        {
            PlayerCombatStats updatedStats = new()
            {
                Strength = request.Strength ?? _runtime.Player.Combatant.BaseStats.Strength,
                AttackSpeed = request.AttackSpeed ?? _runtime.Player.Combatant.BaseStats.AttackSpeed,
                Vitality = request.Vitality ?? _runtime.Player.Combatant.BaseStats.Vitality
            };

            if (_remoteClient != null)
            {
                _remoteClient.SendPlayerActionAsync(new PlayerActionMessage(
                    new PlayerSessionId(_remoteClient.SessionId),
                    _actionSequences.NextActionSequence(),
                    new PlayerStatsUpdateIntent(PlayerCombatStatsSnapshot.From(updatedStats)))).AsTask().GetAwaiter().GetResult();
            }
            else
            {
                _runtime.Player.Combatant.ApplyBaseStats(updatedStats);
            }

            persistDesktopConfig = true;
        }

        if (request.FloatingCombatText.HasValue)
        {
            _settings.ApplyFloatingCombatTextTuning(request.FloatingCombatText.Value);
            persistDesktopConfig = true;
        }

        if (request.EquipInventorySlotIndex.HasValue && _remoteClient != null)
        {
            _remoteClient.SendPlayerActionAsync(new PlayerActionMessage(
                new PlayerSessionId(_remoteClient.SessionId),
                _actionSequences.NextActionSequence(),
                new InventoryEquipIntent(request.EquipInventorySlotIndex.Value))).AsTask().GetAwaiter().GetResult();
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
            _runtime.RebuildMapForFloorSize(_settings.FloorSize);
        }

        if (persistDesktopConfig)
        {
            PersistDesktopClientConfig();
        }
    }

    private void PersistDesktopClientConfig()
    {
        if (_desktopClientConfig == null || _persistDesktopClientConfig == null)
        {
            return;
        }

        _desktopClientConfig.CaptureRuntime(_settings, _runtime.Player.Combatant.BaseStats);
        _persistDesktopClientConfig();
    }

    private static bool IsMoving(PlayerMoveIntent moveIntent)
    {
        return MathF.Abs(moveIntent.ForwardAxis) > 0.001f ||
            MathF.Abs(moveIntent.StrafeAxis) > 0.001f ||
            MathF.Abs(moveIntent.VerticalAxis) > 0.001f;
    }

    private void ApplyInventorySnapshot(PlayerInventorySnapshot? inventory)
    {
        _inventory = inventory;
        string heldItemId;
        ToolStatModifier modifier = default;
        if (inventory?.EquippedSlot?.Item is InventoryItemSnapshot equippedItem)
        {
            heldItemId = equippedItem.VisualId;
            modifier = ToolCatalog.Resolve(equippedItem.ItemKind).StatModifier;
        }
        else
        {
            heldItemId = ViewModelAssets.DefaultLoadout.HeldItemId;
        }

        _runtime.Player.Combatant.ApplyEquippedToolModifier(modifier);
        _viewModelController.SetHeldItem(heldItemId);
    }
}
