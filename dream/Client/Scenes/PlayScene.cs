using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
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
public class PlayScene : IDisposable, ISessionEndSource
{
    private const string LoopbackRemotePlayersEnvironmentVariable = "DREAM_LOOPBACK_REMOTE_PLAYERS";
    private const string RenderPerformanceLogEnvironmentVariable = "DREAM_RENDER_PERF_LOG";
    private static readonly TimeSpan DiagnosticsLogInterval = TimeSpan.FromSeconds(1);
    // Model uploads per frame. Each is one model's GL buffers and textures.
    private const int ModelUploadsPerFrame = 2;

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
    private readonly bool _renderPerformanceLoggingEnabled;

    // The client's replicated world and predicted local player; authority lives on the server.
    // PlayScene remains the desktop composition root plus client presentation owner.
    private readonly ClientWorldSession _runtime;
    // One command per 64 Hz prediction tick, its sequence the tick (NETR-4).
    private readonly ClientCommandTickStream _commandTicks = new(PlayerSessionId.LocalDefault);
    private readonly bool _enableLoopbackRemotePlayers;
    private readonly ClientActionSequenceTracker _actionSequences = new();
    private readonly ClientPredictionReconciler _predictionReconciler = new();
    private readonly ServerTickClock _serverTickClock = new();
    private bool _wasSettingsTogglePressed;
    private bool _wasInventoryTogglePressed;
    private bool _wasCameraViewTogglePressed;
    private bool _localPlayerWasMoving;
    private double _clientPresentationTimeSeconds;
    private double _renderPerformanceLogSeconds;
    private double _lastPresentedFrameMilliseconds;
    private double _lastSwapBuffersMilliseconds;
    private double _lastHudDrawMilliseconds;
    // GPU time per section, read back a few frames late (created on the first Render, with the
    // context current), and the GL call counts of the previous complete frame.
    private GpuFrameTimer? _gpuTimer;
    private RenderCounterSnapshot _lastFrameCounters;
    private PlayerInventorySnapshot? _inventory;
    private bool _disposed;

    // Inputs and output of the prediction tick for the current frame. The tick stream calls a
    // static lambda with this scene as its state, so these are fields rather than captured locals.
    private PlayerSimulationTuning _tickTuning;
    private bool _tickPrimaryUseTriggered;

    // The part of Render before the world draw: GL.Clear, presentation capture and the
    // remote-player states. Reported in the frame snapshot and the render-perf log line.
    private double _lastClearMilliseconds;
    private double _lastPresentationCaptureMilliseconds;
    private double _lastRemotePlayerStatesMilliseconds;

    // Diagnostics keys for the remote session, rebuilt only when the session id changes.
    private string? _diagnosticsKeySessionId;
    private string _snapshotLogKey = string.Empty;
    private string _commandLogKey = string.Empty;
    private string _presenterStateLogKey = string.Empty;

    private double RemotePlayerRenderTick => _serverTickClock.RenderTick;

    public CursorState DesiredCursorState => _settings.CurrentRuntimeState.DesiredCursorState;

    public Vector3 LastReportedPlayerPosition => _runtime.Player.LastReportedPosition;

    public string ConnectedServerEndpoint => _remoteClient.Options.EndpointLabel;

    // Why the session ended (a lost connection, a server silent for 8 s, a rejected join, or a
    // join that timed out), or null while it is live. Update reports it once on the console and in
    // the network log; Game then disposes this scene and returns to the connection screen, which
    // shows it (NETR-2).
    public string? SessionEndReason => _remoteClient.IsConnected
        ? null
        : _remoteClient.DisconnectReason ?? "Disconnected from the server.";

    private bool _sessionEndReported;

    // The endpoint and name this scene's session used, so the connection screen Game returns to
    // after the session ends is filled in for a reconnect.
    public RemoteClientOptions SessionOptions => _remoteClient.Options;

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
        // A send stalled past the server's repeat window makes the prediction ticks idle, as the
        // server does (ClientCommandTickStream); so does an ack lag past the round trip (a network stall).
        _commandTicks.ObserveSendBacklog(() => _remoteClient.IsConnected ? _remoteClient.PendingCommandCount : 0);
        _commandTicks.ObserveAcknowledgedSequence(() => _remoteClient.IsConnected ? ClientCommandTickStream.AcknowledgedSequence(_clientProtocol.Context.LastSnapshot, _remoteClient.SessionId) : null);
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
        PreloadModels();
        _combatFeedback = new CombatFeedbackSystem(_events, new FloatingCombatTextSystem());
        _actionFeedback = new AuthoritativeActionFeedbackSystem(_events);
        _diagnostics = new MultiplayerDiagnostics();

        NetworkDiagnosticsLog.Write(
            "client.scene.start",
            $"remote=True endpoint={ConnectedServerEndpoint} localPresenterSession={_remoteClient.SessionId}");
    }

    // Starts the background decodes of the shared models the scene draws, so they are usually
    // ready within the first frames. The player model is not preloaded here: players draw
    // per-instance models, which LocalPlayerRenderer and RemotePlayerRenderer request themselves,
    // so a shared player model would be uploaded and never drawn.
    private static void PreloadModels()
    {
        ModelAssets.Preload(
            ModelAssets.WolfPath,
            ModelAssets.GrassCubePath,
            ModelAssets.DirtCubePath,
            ModelAssets.StoneCubePath);
        IReadOnlyList<ViewModelDefinition> viewModels = ViewModelAssets.BuiltInDefinitions;
        for (int i = 0; i < viewModels.Count; i++)
        {
            ModelAssets.Preload(viewModels[i].ModelPath);
        }
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
        // The frame only captures input; each prediction tick below turns it into one command
        // whose sequence is that tick.
        PlayerCommandFrame rawCommandFrame = PlayerController.CaptureCommandFrame(
            _runtime.LocalPlayerSessionId,
            0,
            keyboardState,
            mouseState,
            allowGameplayInput);
        rawCommandFrame = _gameplayInputResumeFilter.Filter(rawCommandFrame, allowGameplayInput);
        PlayerCommandFrame commandFrame = _commandInputFilter.Filter(rawCommandFrame);
        _localPlayerWasMoving = IsMoving(commandFrame.MoveIntent);
        _commandTicks.CaptureInput(commandFrame);
        ApplyImmediateLocalLook(commandFrame, allowGameplayInput);
        // Predict with the server's movement values; a local NoclipSpeed the server does not use
        // makes every snapshot disagree with the prediction.
        _tickTuning = _settings.ToPlayerSimulationTuning().WithAuthoritativeMovement();
        _tickPrimaryUseTriggered = false;
        _commandTicks.Advance(
            args.Time,
            this,
            static (scene, command, fixedDeltaSeconds) => scene.RunPredictionTick(command, fixedDeltaSeconds));

        if (_tickPrimaryUseTriggered)
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

    // One prediction tick and its one command: the server gets every tick's command, in order,
    // and the prediction recorded for the same sequence is what its acknowledgement is compared to.
    private void RunPredictionTick(PlayerCommandFrame command, float fixedDeltaSeconds)
    {
        ulong tick = command.Sequence;
        _actionSequences.ObserveCommandSequence(tick);
        QueueRemoteCommandFrame(command);

        // The simulation frame has primary use stripped (the server owns the action), so the
        // local animation is driven from the held input instead. Before, it never played.
        PlayerCommandFrame simulationCommandFrame = ClientSimulationCommandPolicy.PrepareForLocalSimulation(
            command,
            useRemoteAuthority: true);
        bool playPrimaryUse = _runtime.AdvancePredictionTick(
            tick,
            fixedDeltaSeconds,
            simulationCommandFrame,
            _tickTuning,
            primaryUseHeld: command.PrimaryUsePressed);
        _predictionReconciler.RecordPrediction(tick, _runtime.Player.GetWorldPosition(), command.MoveIntent);
        if (playPrimaryUse)
        {
            _tickPrimaryUseTriggered = true;
        }
    }

    public void Render(FrameEventArgs args, bool windowFocused, MouseState mouseState, Action<Vector2i> applyClientSize)
    {
        // Render primarily consumes scene/runtime state for desktop presentation, even though
        // this path still applies scene-owned runtime requests such as floor rebuilds today.
        // Uploads a few background-decoded models before anything draws this frame; draws only
        // poll the model cache, so a model never loads synchronously inside a draw call.
        // The counters cover the previous frame from here to here, HUD included.
        _lastFrameCounters = RenderCounters.CaptureAndReset();
        GpuFrameTimer gpuTimer = _gpuTimer ??= new GpuFrameTimer(GlGpuQueryApi.Instance);
        gpuTimer.BeginFrame();
        ModelAssets.PumpUploads(ModelUploadsPerFrame);
        long clearStart = Stopwatch.GetTimestamp();
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        // The world pass sets its own state rather than inheriting whatever the last pass (the
        // HUD's ImGui restore, or the startup menu) left: opaque, depth-tested geometry.
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
        long captureStart = Stopwatch.GetTimestamp();
        WorldSessionPresentationState presentation = _runtime.CapturePresentationState(
            WorldVisibility.MaxRenderDistance,
            (float)_commandTicks.RemainingSeconds,
            _settings.CameraViewMode);
        long remoteStatesStart = Stopwatch.GetTimestamp();
        // A live list owned by the presenter; it is only read within this frame.
        IReadOnlyList<RemotePlayerPresentationState> remotePlayerStates = _scenePresenter.BuildRemotePlayerStates(
            RemotePlayerRenderTick);

        long worldDrawStart = Stopwatch.GetTimestamp();
        gpuTimer.BeginSection(GpuTimerSection.World);
        WorldRenderStats worldRenderStats = _render.World.DrawWorld(presentation);
        // Actors runs from the enemies through the debug and viewmodel draws, up to the HUD.
        // BeginSection ends the open section first, so the timer queries never nest.
        gpuTimer.BeginSection(GpuTimerSection.Actors);
        long enemyDrawStart = Stopwatch.GetTimestamp();
        _render.Enemies.DrawAtRenderTick(
            presentation.Camera,
            presentation.ActiveEnemies,
            (float)args.Time,
            _settings.ShowCollisionGeometry,
            _scenePresenter,
            RemotePlayerRenderTick);
        long enemyDrawEnd = Stopwatch.GetTimestamp();
        _lastClearMilliseconds = Stopwatch.GetElapsedTime(clearStart, captureStart).TotalMilliseconds;
        _lastPresentationCaptureMilliseconds = Stopwatch.GetElapsedTime(captureStart, remoteStatesStart).TotalMilliseconds;
        _lastRemotePlayerStatesMilliseconds = Stopwatch.GetElapsedTime(remoteStatesStart, worldDrawStart).TotalMilliseconds;

        double localPlayerDrawMilliseconds = 0d;
        if (_settings.CameraViewMode == CameraViewMode.ThirdPerson)
        {
            long localPlayerDrawStart = Stopwatch.GetTimestamp();
            _render.LocalPlayers.Draw(
                new LocalPlayerPresentationState(
                    _runtime.Player.GetWorldPosition(),
                    _runtime.Player.Camera.HorizontalForward,
                    _localPlayerWasMoving),
                presentation.Camera,
                (float)args.Time);
            localPlayerDrawMilliseconds = Stopwatch.GetElapsedTime(localPlayerDrawStart).TotalMilliseconds;
        }

        long remotePlayerDrawStart = Stopwatch.GetTimestamp();
        RemotePlayerRenderStats remoteRenderStats = _render.RemotePlayers.Draw(
            presentation.Camera,
            remotePlayerStates,
            (float)args.Time);
        double remotePlayerDrawMilliseconds = Stopwatch.GetElapsedTime(remotePlayerDrawStart).TotalMilliseconds;
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
            WorldDrawMilliseconds = Stopwatch.GetElapsedTime(worldDrawStart, enemyDrawStart).TotalMilliseconds,
            EnemyDrawMilliseconds = Stopwatch.GetElapsedTime(enemyDrawStart, enemyDrawEnd).TotalMilliseconds,
            LocalPlayerDrawMilliseconds = localPlayerDrawMilliseconds,
            RemotePlayerDrawMilliseconds = remotePlayerDrawMilliseconds,
            HudDrawMilliseconds = _lastHudDrawMilliseconds,
            SwapBuffersMilliseconds = _lastSwapBuffersMilliseconds,
            ServerTickMilliseconds = serverPerformance.TickMilliseconds,
            ServerSnapshotMilliseconds = serverPerformance.SnapshotMilliseconds,
            ServerBroadcastMilliseconds = serverPerformance.BroadcastMilliseconds,
            PreDrawMilliseconds = _lastClearMilliseconds + _lastPresentationCaptureMilliseconds + _lastRemotePlayerStatesMilliseconds,
            PreDrawClearMilliseconds = _lastClearMilliseconds,
            PreDrawCaptureMilliseconds = _lastPresentationCaptureMilliseconds,
            PreDrawRemoteStatesMilliseconds = _lastRemotePlayerStatesMilliseconds,
            GpuWorldMilliseconds = gpuTimer.Latest.WorldMilliseconds,
            GpuActorsMilliseconds = gpuTimer.Latest.ActorsMilliseconds,
            GpuHudMilliseconds = gpuTimer.Latest.HudMilliseconds,
            GpuPrimitives = gpuTimer.Latest.PrimitivesGenerated,
            DrawCalls = _lastFrameCounters.DrawCalls,
            ProgramBinds = _lastFrameCounters.ProgramBinds,
            UniformUploads = _lastFrameCounters.UniformUploads
        };
        if (_settings.ShowCollisionGeometry)
        {
            long debugDrawStart = Stopwatch.GetTimestamp();
            _render.Debug.DrawCollisionBodies(presentation.MapCollisionBodies, presentation.Camera);
            if (presentation.PlayerCollisionBody != null)
            {
                _render.DrawCollisionBody(presentation.PlayerCollisionBody, presentation.Camera);
            }
            performance = performance with
            {
                DebugDrawMilliseconds = Stopwatch.GetElapsedTime(debugDrawStart).TotalMilliseconds
            };
        }

        if (_settings.CameraViewMode == CameraViewMode.FirstPerson)
        {
            long viewModelDrawStart = Stopwatch.GetTimestamp();
            _render.ViewModels.Draw(_viewModelController.CurrentFrameState);
            performance = performance with
            {
                ViewModelDrawMilliseconds = Stopwatch.GetElapsedTime(viewModelDrawStart).TotalMilliseconds
            };
        }

        long hudDrawStart = Stopwatch.GetTimestamp();
        gpuTimer.BeginSection(GpuTimerSection.Hud);
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
                performance,
                _scenePresenter,
                RemotePlayerRenderTick);
        gpuTimer.EndFrame();
        _lastHudDrawMilliseconds = Stopwatch.GetElapsedTime(hudDrawStart).TotalMilliseconds;
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

    // Runs on the render thread while the GL context is still current (Game.OnUnload disposes the
    // scene before base.OnUnload). GL is released first: Render, then the shared model cache, which
    // world cubes, enemies and viewmodels only reference for drawing, so it must outlive the last
    // draw and Render's renderers. The cache is cleared, so a later scene reloads the models.
    // Each step runs even if an earlier one throws, so a network failure cannot skip GL cleanup
    // or the reverse; the failures are rethrown at the end.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<Exception>? failures = null;
        RunDisposeStep(_render.Dispose, ref failures);
        RunDisposeStep(DisposeGpuTimer, ref failures);
        RunDisposeStep(ModelAssets.DisposeAll, ref failures);
        RunDisposeStep(_combatFeedback.Dispose, ref failures);
        RunDisposeStep(_actionFeedback.Dispose, ref failures);
        RunDisposeStep(_runtime.Dispose, ref failures);
        RunDisposeStep(DisposeRemoteClient, ref failures);

        if (failures == null)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(failures);
    }

    private void DisposeGpuTimer()
    {
        _gpuTimer?.Dispose();
        _gpuTimer = null;
    }

    private static void RunDisposeStep(Action step, ref List<Exception>? failures)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }
    }

    private void DisposeRemoteClient()
    {
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
            if (NetworkDiagnosticsLog.IsEnabled)
            {
                RefreshDiagnosticsKeys();
                NetworkDiagnosticsLog.WriteEvery(
                    _snapshotLogKey,
                    DiagnosticsLogInterval,
                    "client.apply.snapshot",
                    (SessionId: _remoteClient.SessionId, snapshotMessage.Snapshot),
                    static state => $"local={state.SessionId} {NetworkDiagnosticsLog.DescribeSnapshot(state.Snapshot)}");
            }

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
            if (NetworkDiagnosticsLog.IsEnabled)
            {
                NetworkDiagnosticsLog.Write(
                    "client.apply.action",
                    $"local={_remoteClient.SessionId} resultSession={actionMessage.Result.SessionId} " +
                    $"seq={actionMessage.Result.ActionSequence} accepted={actionMessage.Result.Accepted} " +
                    $"payload={actionMessage.Result.Payload?.GetType().Name ?? "none"}");
            }

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
            string.Join(" | ", performance.ToHudLines()) +
            $" | clear={_lastClearMilliseconds:0.00}ms capture={_lastPresentationCaptureMilliseconds:0.00}ms " +
            $"remoteStates={_lastRemotePlayerStatesMilliseconds:0.00}ms");
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

        if (!_sessionEndReported && !_remoteClient.IsConnected)
        {
            ReportSessionEnd();
        }
    }

    // Once per scene, off the steady-state frame path.
    private void ReportSessionEnd()
    {
        _sessionEndReported = true;
        string reason = SessionEndReason ?? "Disconnected from the server.";
        Console.WriteLine($"[session] ended: {reason}");
        NetworkDiagnosticsLog.Write(
            "client.scene.session-end",
            $"session={_remoteClient.SessionId} reason={reason}");
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
        if (NetworkDiagnosticsLog.IsEnabled)
        {
            RefreshDiagnosticsKeys();
            NetworkDiagnosticsLog.WriteEvery(
                _commandLogKey,
                DiagnosticsLogInterval,
                "client.scene.command",
                (Scene: this, Frame: remoteCommandFrame),
                static state => state.Scene.DescribeQueuedCommand(state.Frame));
        }
    }

    private string DescribeQueuedCommand(PlayerCommandFrame remoteCommandFrame)
    {
        return $"session={_remoteClient.SessionId} seq={remoteCommandFrame.Sequence} sent={_remoteClient.SentCommandCount} " +
            $"facing={NetworkDiagnosticsLog.FormatVector(remoteCommandFrame.LookIntent.Facing ?? _runtime.Player.Camera.Front)}";
    }

    // Diagnostics keys are only needed while diagnostics are on; they are rebuilt only when the
    // remote session id differs from the one they were built for.
    private void RefreshDiagnosticsKeys()
    {
        string sessionId = _remoteClient.SessionId;
        if (string.Equals(sessionId, _diagnosticsKeySessionId, StringComparison.Ordinal))
        {
            return;
        }

        _diagnosticsKeySessionId = sessionId;
        _snapshotLogKey = $"client.apply.snapshot.{sessionId}";
        _commandLogKey = $"client.scene.command.{sessionId}";
        _presenterStateLogKey = $"client.presenter.state.{sessionId}";
    }

    private void LogPresenterDiagnostics(string source)
    {
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            return;
        }

        RefreshDiagnosticsKeys();
        if (!NetworkDiagnosticsLog.ShouldLogEvery(_presenterStateLogKey, DiagnosticsLogInterval))
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
