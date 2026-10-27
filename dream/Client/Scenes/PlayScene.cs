using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Application;
using Dream.Client.Input;
using Dream.Client.Networking;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Client.Startup;
using Dream.Client.World;
using Dream.Configuration;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Actors;
using Dream.Rendering.Core;
using Dream.Rendering.Hud;
using Dream.Rendering.ViewModels;
using Dream.Rendering.World;
using Dream.Server;
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

    // Client-local presentation state that should remain scene-owned after the runtime split.
    private readonly Render _render;
    private readonly GameSettings _settings;
    private readonly DesktopClientConfig? _desktopClientConfig;
    private readonly Action? _persistDesktopClientConfig;
    private readonly ILocalServerHandle? _localServer;
    private readonly PlayerViewModelController _viewModelController;
    private readonly SceneEventHub _events = new();
    private readonly CombatFeedbackSystem _combatFeedback;
    private readonly AuthoritativeActionFeedbackSystem _actionFeedback;
    private readonly MultiplayerDiagnostics _diagnostics;
    private readonly ClientProtocolSession _clientProtocol;
    private readonly ClientScenePresenter _scenePresenter;
    private readonly RemoteClientSession _remoteClient;
    private readonly GameplayInputResumeFilter _gameplayInputResumeFilter = new();
    private readonly PlayerCommandOneShotInputFilter _commandInputFilter = new();
    private readonly LatestPlayerCommandInbox _localSimulationCommandInbox = new();
    private readonly bool _renderPerformanceLoggingEnabled;

    // The client's replicated world and predicted local player; authority lives on the server.
    // PlayScene remains the desktop composition root plus client presentation owner.
    private readonly ClientWorldSession _runtime;
    private readonly ServerTickRunner _tickRunner = new();
    private readonly bool _enableLoopbackRemotePlayers;
    private readonly ClientActionSequenceTracker _actionSequences = new();
    private readonly ClientPredictionReconciler _predictionReconciler = new();
    private readonly ServerTickClock _serverTickClock = new();
    private ulong _nextLocalCommandSequence = 1;
    private PlayerCommandFrame? _lastLocalSimulationCommandFrame;
    private bool _wasSettingsTogglePressed;
    private bool _wasInventoryTogglePressed;
    private bool _wasCameraViewTogglePressed;
    private bool _localPlayerWasMoving;
    private double _clientPresentationTimeSeconds;
    private double _renderPerformanceLogSeconds;
    private double _lastPresentedFrameMilliseconds;
    private double _lastSwapBuffersMilliseconds;
    private double _lastHudDrawMilliseconds;
    private PlayerInventorySnapshot? _inventory;

    private double RemotePlayerRenderTick => _serverTickClock.RenderTick;

    public CursorState DesiredCursorState => _settings.CurrentRuntimeState.DesiredCursorState;

    public Vector3 LastReportedPlayerPosition => _runtime.Player.LastReportedPosition;

    public string ConnectedServerEndpoint => _remoteClient.Options.EndpointLabel;

    internal SceneEventHub Events => _events;

    public PlayScene(
        GameSettings settings,
        RemoteClientSession remoteClient,
        DesktopClientConfig? clientConfig = null,
        Action? persistDesktopClientConfig = null,
        bool enableLoopbackRemotePlayers = false,
        ILocalServerHandle? localServer = null)
    {
        _settings = settings;
        _desktopClientConfig = clientConfig;
        _persistDesktopClientConfig = persistDesktopClientConfig;
        _localServer = localServer;
        _enableLoopbackRemotePlayers = enableLoopbackRemotePlayers;
        _remoteClient = remoteClient ?? throw new ArgumentNullException(nameof(remoteClient));
        _renderPerformanceLoggingEnabled = IsRenderPerformanceLoggingEnabled();
        // The desktop scene owns a client world (replicated map + predicted player); the server
        // owns authority. PlayScene remains the composition root for presentation systems.
        _viewModelController = new PlayerViewModelController(ViewModelCatalog.CreateDefault(), _settings);
        _clientProtocol = ClientProtocolSession.CreateDefault();

        Vector3 playerStartPosition = new Vector3(5, 1.5f, 5);
        _runtime = new ClientWorldSession(
            _settings.FloorSize,
            playerStartPosition,
            _events);
        if (_desktopClientConfig != null)
        {
            _runtime.Player.Combatant.ApplyStats(_desktopClientConfig.ToPlayerCombatStats());
        }

        _scenePresenter = new ClientScenePresenter(_remoteClient.SessionId);
        _render = new Render(_settings);
        _combatFeedback = new CombatFeedbackSystem(_events, new FloatingCombatTextSystem());
        _actionFeedback = new AuthoritativeActionFeedbackSystem(_events);
        _diagnostics = new MultiplayerDiagnostics();

        NetworkDiagnosticsLog.Write(
            "client.scene.start",
            $"remote=True endpoint={ConnectedServerEndpoint} localPresenterSession={_remoteClient.SessionId}");
    }

    public void Update(FrameEventArgs args, KeyboardState keyboardState, MouseState mouseState)
    {
        float delta = (float)args.Time;
        _clientPresentationTimeSeconds += args.Time;
        _serverTickClock.Advance(args.Time);
        DrainRemoteTransportMessages();
        _runtime.Player.NudgeWorldPosition(_predictionReconciler.TakeSmoothingStep(delta));

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
        // Predict with the server's movement values; a local NoclipSpeed the server does not use
        // makes every snapshot disagree with the prediction.
        PlayerSimulationTuning tuning = PlayerSimulationTuning.FromSettings(_settings).WithAuthoritativeMovement();
        bool primaryUseTriggered = false;
        _tickRunner.Advance(args.Time, (tick, fixedDeltaSeconds) =>
        {
            PlayerCommandFrame simulationCommandFrame = ResolveLocalSimulationCommandFrame(
                commandFrame,
                out PlayerCommandFrame? freshSourceFrame);
            if (freshSourceFrame.HasValue)
            {
                // One merged command per client tick: the server consumes at most one per tick,
                // so per-frame sends only cost decoding and lock contention.
                QueueRemoteCommandFrame(freshSourceFrame.Value);
            }

            // The simulation frame has primary use stripped (the server owns the action), so the
            // local animation is driven from the held input instead. Before, it never played.
            bool playPrimaryUse = _runtime.AdvancePredictionTick(
                tick,
                fixedDeltaSeconds,
                simulationCommandFrame,
                tuning,
                primaryUseHeld: _lastLocalSimulationCommandFrame?.PrimaryUsePressed ?? false);
            _predictionReconciler.RecordPrediction(
                simulationCommandFrame.Sequence,
                _runtime.Player.GetWorldPosition());
            if (playPrimaryUse)
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
            RemotePlayerRenderTick);

        Stopwatch worldDrawStopwatch = Stopwatch.StartNew();
        WorldRenderStats worldRenderStats = _render.World.DrawWorld(presentation);
        worldDrawStopwatch.Stop();
        Stopwatch enemyDrawStopwatch = Stopwatch.StartNew();
        _render.Enemies.Draw(
            presentation.Camera,
            presentation.ActiveEnemies,
            (float)args.Time,
            _settings.ShowCollisionGeometry);
        enemyDrawStopwatch.Stop();

        double localPlayerDrawMilliseconds = 0d;
        if (_settings.CameraViewMode == CameraViewMode.ThirdPerson)
        {
            Stopwatch localPlayerStopwatch = Stopwatch.StartNew();
            _render.LocalPlayers.Draw(
                new LocalPlayerPresentationState(
                    _runtime.Player.GetWorldPosition(),
                    _runtime.Player.Camera.HorizontalForward,
                    _localPlayerWasMoving),
                presentation.Camera,
                (float)args.Time);
            localPlayerStopwatch.Stop();
            localPlayerDrawMilliseconds = localPlayerStopwatch.Elapsed.TotalMilliseconds;
        }

        Stopwatch remotePlayerStopwatch = Stopwatch.StartNew();
        RemotePlayerRenderStats remoteRenderStats = _render.RemotePlayers.Draw(
            presentation.Camera,
            remotePlayerStates,
            (float)args.Time);
        remotePlayerStopwatch.Stop();
        LogRemotePlayerRenderDiagnostics(presentation.Camera, remotePlayerStates, remoteRenderStats);

        DedicatedServerPerformanceSnapshot serverPerformance =
            _localServer?.LatestPerformance ?? DedicatedServerPerformanceSnapshot.Empty;
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
            worldRenderStats.StaticChunkReusedMeshes) with
        {
            TotalFrameMilliseconds = _lastPresentedFrameMilliseconds,
            WorldDrawMilliseconds = worldDrawStopwatch.Elapsed.TotalMilliseconds,
            EnemyDrawMilliseconds = enemyDrawStopwatch.Elapsed.TotalMilliseconds,
            LocalPlayerDrawMilliseconds = localPlayerDrawMilliseconds,
            RemotePlayerDrawMilliseconds = remotePlayerStopwatch.Elapsed.TotalMilliseconds,
            HudDrawMilliseconds = _lastHudDrawMilliseconds,
            SwapBuffersMilliseconds = _lastSwapBuffersMilliseconds,
            ServerTickMilliseconds = serverPerformance.TickMilliseconds,
            ServerSnapshotMilliseconds = serverPerformance.SnapshotMilliseconds,
            ServerBroadcastMilliseconds = serverPerformance.BroadcastMilliseconds
        };
        if (_settings.ShowCollisionGeometry)
        {
            Stopwatch debugDrawStopwatch = Stopwatch.StartNew();
            _render.Debug.DrawCollisionBodies(presentation.MapCollisionBodies, presentation.Camera);
            if (presentation.PlayerCollisionBody != null)
            {
                _render.DrawCollisionBody(presentation.PlayerCollisionBody, presentation.Camera);
            }
            debugDrawStopwatch.Stop();
            performance = performance with
            {
                DebugDrawMilliseconds = debugDrawStopwatch.Elapsed.TotalMilliseconds
            };
        }

        if (_settings.CameraViewMode == CameraViewMode.FirstPerson)
        {
            Stopwatch viewModelDrawStopwatch = Stopwatch.StartNew();
            _render.ViewModels.Draw(_viewModelController.CurrentFrameState);
            viewModelDrawStopwatch.Stop();
            performance = performance with
            {
                ViewModelDrawMilliseconds = viewModelDrawStopwatch.Elapsed.TotalMilliseconds
            };
        }

        Stopwatch hudStopwatch = Stopwatch.StartNew();
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
        hudStopwatch.Stop();
        _lastHudDrawMilliseconds = hudStopwatch.Elapsed.TotalMilliseconds;
        LogRenderPerformanceIfEnabled(args, performance, windowFocused);
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
        _remoteClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public void ApplyReplicatedMessage(IServerToClientMessage message)
    {
        _clientProtocol.ApplyServerMessage(message);
        if (message is JoinAcceptedMessage joinAcceptedMessage)
        {
            NetworkDiagnosticsLog.Write(
                "client.apply.join",
                $"local={_remoteClient.SessionId} {NetworkDiagnosticsLog.DescribeJoin(joinAcceptedMessage)}");
            ClientJoinAcceptedApplier.ApplyToRuntime(_runtime, joinAcceptedMessage);
            _predictionReconciler.Reset();
            _serverTickClock.Reset(joinAcceptedMessage.CurrentServerTick);
            ClientJoinAcceptedApplier.ApplyToPresenter(_scenePresenter, joinAcceptedMessage);
            ApplyInventorySnapshot(joinAcceptedMessage.Inventory);
            LogPresenterDiagnostics("join");
        }
        else if (message is ServerSnapshotMessage snapshotMessage)
        {
            NetworkDiagnosticsLog.WriteEvery(
                $"client.apply.snapshot.{_remoteClient.SessionId}",
                TimeSpan.FromSeconds(1),
                "client.apply.snapshot",
                () => $"local={_remoteClient.SessionId} {NetworkDiagnosticsLog.DescribeSnapshot(snapshotMessage.Snapshot)}");
            RemoteWorldMutationApplier.ApplySnapshotToRuntime(_runtime, snapshotMessage.Snapshot);
            RequestWorldResyncIfOutOfSync(snapshotMessage.Snapshot);
            ClientCorrectionApplier.ApplySnapshotToRuntime(
                _runtime,
                snapshotMessage.Snapshot,
                _remoteClient.SessionId,
                _predictionReconciler);
            RemoteEnemyStateApplier.ApplyActorSnapshots(_runtime.Enemies, snapshotMessage.Snapshot.Actors);
            ClientSnapshotDamageApplier.ApplyToRuntime(_runtime, _events, snapshotMessage.Snapshot);
            _diagnostics.RecordSnapshotApplied(snapshotMessage.Snapshot, _remoteClient.SessionId);

            _serverTickClock.Observe(snapshotMessage.Snapshot.ServerTick);
            _scenePresenter.ApplySnapshot(snapshotMessage.Snapshot);
            LogPresenterDiagnostics("snapshot");
        }
        else if (message is ServerActionMessage actionMessage)
        {
            NetworkDiagnosticsLog.Write(
                "client.apply.action",
                $"local={_remoteClient.SessionId} resultSession={actionMessage.Result.SessionId} " +
                $"seq={actionMessage.Result.ActionSequence} accepted={actionMessage.Result.Accepted} " +
                $"payload={actionMessage.Result.Payload?.GetType().Name ?? "none"}");
            _actionSequences.ObserveActionResult(actionMessage.Result);
            bool resultTargetsLocalSession = string.Equals(
                actionMessage.Result.SessionId,
                _remoteClient.SessionId,
                StringComparison.Ordinal);
            ClientAuthoritativeActionApplier.ApplyActionResult(
                _runtime,
                _events,
                actionMessage.Result,
                _remoteClient.SessionId,
                _diagnostics);
            if (resultTargetsLocalSession && actionMessage.Result.Correction?.CorrectedPosition != null)
            {
                // A rejected-action correction hard-snaps the player, so earlier predictions no longer apply.
                _predictionReconciler.Reset();
            }

            if (resultTargetsLocalSession && actionMessage.Result.Payload is InventoryChangedResult inventoryChanged)
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

    private void LogRenderPerformanceIfEnabled(FrameEventArgs args, FramePerformanceSnapshot performance, bool windowFocused)
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
            $"focused={windowFocused} settings={_settings.SettingsVisible} inventory={_settings.InventoryVisible} " +
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
        while (_remoteClient.TryDequeueInbound(out IServerToClientMessage? message) && message != null)
        {
            ApplyReplicatedMessage(message);
        }
    }

    public void ReportPresentedFramePerformance(double totalFrameMilliseconds, double swapBuffersMilliseconds)
    {
        _lastPresentedFrameMilliseconds = totalFrameMilliseconds;
        _lastSwapBuffersMilliseconds = swapBuffersMilliseconds;
    }

    private const double WorldResyncRequestCooldownSeconds = 2d;
    private double _lastWorldResyncRequestSeconds = double.NegativeInfinity;

    // A missed or rejected delta used to leave the client's world diverged until reconnect.
    private void RequestWorldResyncIfOutOfSync(ServerSnapshotEnvelope snapshot)
    {
        if (!RemoteWorldMutationApplier.IsWorldOutOfSync(_runtime, snapshot) ||
            _clientPresentationTimeSeconds - _lastWorldResyncRequestSeconds < WorldResyncRequestCooldownSeconds)
        {
            return;
        }

        _lastWorldResyncRequestSeconds = _clientPresentationTimeSeconds;
        ulong clientChecksum = WorldStateChecksum.Compute(_runtime.Map);
        NetworkDiagnosticsLog.Write(
            "client.world.resync-request",
            $"session={_remoteClient.SessionId} expected={snapshot.WorldChecksum} actual={clientChecksum} seq={snapshot.WorldChecksumSequence}");
        _remoteClient.QueueWorldResyncRequest(clientChecksum);
    }

    private void QueueRemoteCommandFrame(PlayerCommandFrame localCommandFrame)
    {
        if (!_remoteClient.IsConnected)
        {
            return;
        }

        PlayerCommandFrame remoteCommandFrame = RemotePlayerCommandRelay.CreateRelayCommand(
            localCommandFrame,
            new PlayerSessionId(_remoteClient.SessionId),
            _runtime.Player.Camera.Front);
        _remoteClient.QueueCommandFrame(remoteCommandFrame);
        NetworkDiagnosticsLog.WriteEvery(
            $"client.scene.command.{_remoteClient.SessionId}",
            TimeSpan.FromSeconds(1),
            "client.scene.command",
            () => $"session={_remoteClient.SessionId} seq={remoteCommandFrame.Sequence} sent={_remoteClient.SentCommandCount} " +
                $"facing={NetworkDiagnosticsLog.FormatVector(remoteCommandFrame.LookIntent.Facing ?? _runtime.Player.Camera.Front)}");
    }

    private void LogPresenterDiagnostics(string source)
    {
        if (!NetworkDiagnosticsLog.ShouldLogEvery(
                $"client.presenter.state.{_remoteClient.SessionId}",
                TimeSpan.FromSeconds(1)))
        {
            return;
        }

        ClientScenePresenterDebugState state = _scenePresenter.CaptureDebugState(
            RemotePlayerRenderTick);
        NetworkDiagnosticsLog.Write(
            "client.presenter.state",
            $"source={source} local={state.LocalSessionId ?? "<none>"} roster={state.RosterCount}[{string.Join(", ", state.RosterSessions)}] " +
            $"remoteEntries={state.RemoteEntryCount}[{string.Join(", ", state.RemoteSessions)}] sampled={state.SampledRemoteCount}");
    }

    private static void LogRemotePlayerRenderDiagnostics(
        Camera camera,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayerStates,
        RemotePlayerRenderStats stats)
    {
        if (!NetworkDiagnosticsLog.ShouldLogEvery("client.render.remote", TimeSpan.FromSeconds(1)))
        {
            return;
        }

        string sessions = string.Join(
            ", ",
            remotePlayerStates.Select(player =>
                $"{player.SessionId}#{player.ActorId}/{player.DisplayName}@{NetworkDiagnosticsLog.FormatVector(player.Pose.Position)} alive={player.IsAlive}"));
        NetworkDiagnosticsLog.Write(
            "client.render.remote",
            $"camera={NetworkDiagnosticsLog.FormatVector(camera.Position)} input={stats.InputCount} alive={stats.AliveCount} " +
            $"visible={stats.VisibleCount} cachedModels={stats.ModelCount} players=[{sessions}]");
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

    private PlayerCommandFrame ResolveLocalSimulationCommandFrame(
        PlayerCommandFrame currentFrame,
        out PlayerCommandFrame? freshSourceFrame)
    {
        IReadOnlyList<PlayerCommandFrame> pendingFrames =
            _localSimulationCommandInbox.Drain([currentFrame.SessionId]);
        freshSourceFrame = pendingFrames.Count > 0 ? pendingFrames[^1] : null;
        PlayerCommandFrame simulationSource = freshSourceFrame
            ?? _lastLocalSimulationCommandFrame
            ?? ClearOneShotMoveInputs(currentFrame);

        _lastLocalSimulationCommandFrame = ClearOneShotMoveInputs(simulationSource);
        return ClientSimulationCommandPolicy.PrepareForLocalSimulation(
            simulationSource,
            useRemoteAuthority: true);
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

            _remoteClient.QueuePlayerAction(new PlayerActionMessage(
                new PlayerSessionId(_remoteClient.SessionId),
                _actionSequences.NextActionSequence(),
                new PlayerStatsUpdateIntent(PlayerCombatStatsSnapshot.From(updatedStats))));

            persistDesktopConfig = true;
        }

        if (request.FloatingCombatText.HasValue)
        {
            _settings.ApplyFloatingCombatTextTuning(request.FloatingCombatText.Value);
            persistDesktopConfig = true;
        }

        if (request.EquipInventorySlotIndex.HasValue)
        {
            _remoteClient.QueuePlayerAction(new PlayerActionMessage(
                new PlayerSessionId(_remoteClient.SessionId),
                _actionSequences.NextActionSequence(),
                new InventoryEquipIntent(request.EquipInventorySlotIndex.Value)));
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
