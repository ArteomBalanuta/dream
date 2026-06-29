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
    private const string LoopbackRemotePlayersEnvironmentVariable = "DREAM_LOOPBACK_REMOTE_PLAYERS";
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
    private readonly PlayerCommandOneShotInputFilter _commandInputFilter = new();
    private readonly LatestPlayerCommandInbox _localSimulationCommandInbox = new();

    // Authoritative world/player/physics ownership now flows through WorldSessionRuntime.
    // PlayScene remains the desktop composition root plus client presentation owner.
    private readonly WorldSessionRuntime _runtime;
    private readonly ServerTickRunner _tickRunner = new();
    private readonly bool _enableLoopbackRemotePlayers;
    private ulong _nextLocalCommandSequence = 1;
    private ulong _nextLocalActionSequence = 1;
    private PlayerCommandFrame? _lastLocalSimulationCommandFrame;
    private bool _wasSettingsTogglePressed;
    private double _clientPresentationTimeSeconds;

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
        PlayerCommandFrame commandFrame = _commandInputFilter.Filter(rawCommandFrame);
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
            (float)_tickRunner.RemainingSeconds);
        IReadOnlyList<RemotePlayerPresentationState> remotePlayerStates = _scenePresenter.BuildRemotePlayerStates(
            Math.Max(0d, _clientPresentationTimeSeconds - RemotePlayerInterpolationDelaySeconds));

        _render.World.DrawWorld(presentation);
        _render.Enemies.Draw(
            presentation.Camera,
            presentation.ActiveEnemies,
            (float)args.Time,
            _settings.ShowCollisionGeometry);
        _render.RemotePlayers.Draw(
            presentation.Camera,
            remotePlayerStates);

        if (_settings.ShowCollisionGeometry)
        {
            _render.Debug.DrawCollisionBodies(presentation.MapCollisionBodies, presentation.Camera);
            if (presentation.PlayerCollisionBody != null)
            {
                _render.DrawCollisionBody(presentation.PlayerCollisionBody, presentation.Camera);
            }
        }

        _render.ViewModels.Draw(_viewModelController.CurrentFrameState);
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
                ConnectedServerEndpoint);
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
            ClientAuthoritativeActionApplier.ApplyActionResult(_runtime, _events, actionMessage.Result, _diagnostics);
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

    private void HandleGlobalInput(KeyboardState keyboardState)
    {
        bool settingsTogglePressed = keyboardState.IsKeyDown(Keys.Escape);
        if (settingsTogglePressed && !_wasSettingsTogglePressed)
        {
            _settings.ToggleSettings();
        }

        _wasSettingsTogglePressed = settingsTogglePressed;
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

        PlayerCommandFrame remoteCommandFrame = new(
            new PlayerSessionId(_remoteClient.SessionId),
            localCommandFrame.Sequence,
            localCommandFrame.RequestedMode,
            localCommandFrame.MoveIntent,
            localCommandFrame.LookIntent,
            localCommandFrame.PrimaryUsePressed);
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
                Strength = request.Strength ?? _runtime.Player.Combatant.Stats.Strength,
                AttackSpeed = request.AttackSpeed ?? _runtime.Player.Combatant.Stats.AttackSpeed,
                Vitality = request.Vitality ?? _runtime.Player.Combatant.Stats.Vitality
            };
            _runtime.Player.Combatant.ApplyStats(updatedStats);

            if (_remoteClient != null)
            {
                _remoteClient.SendPlayerActionAsync(new PlayerActionMessage(
                    new PlayerSessionId(_remoteClient.SessionId),
                    _nextLocalActionSequence++,
                    new PlayerStatsUpdateIntent(PlayerCombatStatsSnapshot.From(updatedStats)))).AsTask().GetAwaiter().GetResult();
            }

            persistDesktopConfig = true;
        }

        if (request.FloatingCombatText.HasValue)
        {
            _settings.ApplyFloatingCombatTextTuning(request.FloatingCombatText.Value);
            persistDesktopConfig = true;
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

        _desktopClientConfig.CaptureRuntime(_settings, _runtime.Player.Combatant.Stats);
        _persistDesktopClientConfig();
    }
}
