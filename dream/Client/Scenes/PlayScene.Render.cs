using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Diagnostics;
using Dream.Application;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Rendering.Core;
using Dream.Rendering.Hud;
using Dream.Rendering.Sky;
using Dream.Rendering.World;
using Dream.Server;
using Dream.Simulation.World;

namespace Dream.Client.Scenes;

public partial class PlayScene
{
    // Created in the constructor with Render's other GL resources (on the render thread, with the
    // context current); disposed with the scene, which nulls it.
    private SkyRenderer? _sky;
    private readonly SkyModelSettings _skySettings = SkyModelSettings.FromEnvironment();

    public void Render(FrameEventArgs args, bool windowFocused, MouseState mouseState, Action<Vector2i> applyClientSize)
    {
        // Render primarily consumes scene/runtime state for desktop presentation, even though
        // this path still applies scene-owned runtime requests such as floor rebuilds today.
        // Uploads a few background-decoded models before anything draws this frame; draws only
        // poll the model cache, so a model never loads synchronously inside a draw call.
        // The counters cover the previous frame from here to here, HUD included.
        _captureApplyClientSize = applyClientSize;
        _windowFocused = windowFocused;
        _lastFrameCounters = RenderCounters.CaptureAndReset();
        GpuFrameTimer gpuTimer = _gpuTimer ??= new GpuFrameTimer(GlGpuQueryApi.Instance);
        gpuTimer.BeginFrame();
        ModelAssets.PumpUploads(ModelUploadsPerFrame);
        // The sky for this frame: the replicated time of day, unless the client-local debug override
        // replaces it. Its lighting is what the world pass uploads, and the clear colour is the
        // horizon (== the fog colour), so a pixel nothing covers already matches the distance.
        // Evaluated before clearStart, so the clear timing stays the clear alone.
        SkyState sky = SkyModel.Evaluate(_render.TimeOfDayOverride.Resolve(WorldTimeOfDay01), _skySettings);
        _render.SetFrameLighting(in sky.Lighting);
        bool isometric = _settings.CameraViewMode == CameraViewMode.Isometric;
        long clearStart = Stopwatch.GetTimestamp();
        // Isometric shows the map edge, so the void is a dim horizon colour rather than the sky.
        float clearScale = isometric ? 0.25f : 1f;
        GL.ClearColor(sky.HorizonColor.X * clearScale, sky.HorizonColor.Y * clearScale, sky.HorizonColor.Z * clearScale, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        // The world pass sets its own state rather than inheriting whatever the last pass (the
        // HUD's ImGui restore, or the startup menu) left: opaque, depth-tested geometry.
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
        long captureStart = Stopwatch.GetTimestamp();
        WorldSessionPresentationState presentation = _runtime.CapturePresentationState(
            WorldVisibility.MaxRenderDistance,
            (float)_commandTicks.RemainingSeconds,
            _settings.CameraViewMode,
            _deathTracker.IsDead,
            _deathTracker.SecondsRemaining(_serverTickClock.EstimatedTick),
            (float)args.Time);
        if (isometric)
        {
            _render.FogOrigin = _runtime.LastCameraFocus;
            _render.LastWorldCamera = presentation.Camera;
        }
        else
        {
            _render.FogOrigin = null;
            _render.LastWorldCamera = null;
        }

        _render.LootFeed = _lootFeedback;
        long remoteStatesStart = Stopwatch.GetTimestamp();
        // A live list owned by the presenter; it is only read within this frame.
        IReadOnlyList<RemotePlayerPresentationState> remotePlayerStates = _scenePresenter.BuildRemotePlayerStates(
            RemotePlayerRenderTick);

        UpdateAudioFrame(presentation, remotePlayerStates, sky.StarVisibility, (float)args.Time);
        CollectFrameLights(in presentation, remotePlayerStates);
        long worldDrawStart = Stopwatch.GetTimestamp();
        gpuTimer.BeginSection(GpuTimerSection.World);
        WorldRenderStats worldRenderStats = _render.World.DrawWorld(presentation);
        // Actors runs from the enemies through the sky, then the Effects section takes over. A
        // section cannot reopen within a frame, so the debug and viewmodel draws that follow are
        // untimed. BeginSection ends the open section first, so the timer queries never nest.
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
        if (_settings.CameraViewMode != CameraViewMode.FirstPerson)
        {
            long localPlayerDrawStart = Stopwatch.GetTimestamp();
            _render.LocalPlayers.Draw(
                new LocalPlayerPresentationState(
                    _runtime.LocalRenderPose.Position,
                    _runtime.LocalRenderPose.Facing,
                    _runtime.LocalRenderPose.IsMoving),
                presentation.Camera,
                (float)args.Time,
                silhouette: isometric);
            localPlayerDrawMilliseconds = Stopwatch.GetElapsedTime(localPlayerDrawStart).TotalMilliseconds;
        }

        long remotePlayerDrawStart = Stopwatch.GetTimestamp();
        RemotePlayerRenderStats remoteRenderStats = _render.RemotePlayers.Draw(
            presentation.Camera,
            remotePlayerStates,
            (float)args.Time);
        double remotePlayerDrawMilliseconds = Stopwatch.GetElapsedTime(remotePlayerDrawStart).TotalMilliseconds;
        LogRemotePlayerRenderDiagnostics(presentation.Camera, remotePlayerStates, remoteRenderStats);
        DrawEconomyEntities(presentation.Camera);

        // Sky pass, still inside the Actors GPU section: after every opaque actor, so it shades only
        // the pixels left at depth 1, and before the debug and viewmodel passes. It restores the
        // world state (DepthFunc Less, depth writes on, blending off) when it is done.
        SkyRenderer skyRenderer = _sky ?? throw new ObjectDisposedException(nameof(PlayScene));
        if (!isometric)
        {
            skyRenderer.Draw(in sky, presentation.Camera.GetViewMatrix(), _render.ProjectionMatrix);
        }

        DrawEffectsPass(in sky, in presentation, gpuTimer, (float)args.Time);

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
            GpuEffectsMilliseconds = gpuTimer.Latest.EffectsMilliseconds,
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
        _render.WorldTransferHud = CaptureWorldTransferHudState(_remoteClient);
        _render.HudScene = new HudSceneState(LastReportedPlayerPosition);
        AdvanceHudCaptureScript();
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
        CaptureHudFrameIfRequested();
    }

    public void Resize(Vector2i size)
    {
        GL.Viewport(0, 0, size.X, size.Y);
        _settings.ApplyResolution(size);
        _render.Hud.UpdateProjectionForSettings(_settings);
    }

    public void ReportPresentedFramePerformance(double totalFrameMilliseconds, double swapBuffersMilliseconds)
    {
        _lastPresentedFrameMilliseconds = totalFrameMilliseconds;
        _lastSwapBuffersMilliseconds = swapBuffersMilliseconds;
    }
}
