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
using Dream.Rendering.Mist;
using Dream.Rendering.Post;
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

    // The boundary mist wall's void pass (smoke where no surface covers a pixel past the map edge).
    // Null when its shader failed to load; the per-fragment mist in the world shaders still runs.
    private BoundaryMistVoidRenderer? _boundaryMist;
    private readonly BoundaryMistSettings _boundaryMistSettings = BoundaryMistSettings.FromEnvironment();

    // Debug-group names for frame captures (RenderDoc, Nsight); they cost nothing unless GPU markers are on.
    private static readonly GpuDebugLabel ShadowsGroup = new("Shadows");
    private static readonly GpuDebugLabel WorldGroup = new("World");
    private static readonly GpuDebugLabel ActorsGroup = new("Actors");
    private static readonly GpuDebugLabel BoundaryVoidGroup = new("Boundary void");
    private static readonly GpuDebugLabel EffectsGroup = new("Effects");
    private static readonly GpuDebugLabel DebugOverlaysGroup = new("Debug overlays");
    private static readonly GpuDebugLabel ViewModelGroup = new("View model");
    private static readonly GpuDebugLabel HudGroup = new("HUD");

    // The offscreen RGBA16F buffer the world draws into, rebuilt when the window or the granted
    // sample count changes, and the chain that resolves it onto the window. Both null when the
    // chain could not be built or DREAM_POST=0, in which case the frame below is byte for byte the
    // frame this renderer drew before the pass existed. _contextSamples is read once, from the
    // default framebuffer, because GLFW_SAMPLES is a window-creation hint and cannot change.
    private SceneTarget? _sceneTarget;

    // True from BindForScene until the resolve has returned drawing to the window. The guard in
    // Render reads it when the method exits, so a throw in between cannot leave the scene target bound.
    private bool _sceneFramebufferBound;

    // Rebinds the window on every exit path of Render that left the scene target bound. A using
    // declaration, so it covers the rest of the method without re-indenting it, and a readonly
    // struct, so it costs no allocation per frame.
    private readonly struct SceneFramebufferGuard(PlayScene scene) : IDisposable
    {
        public void Dispose() => scene.RestoreWindowIfSceneBound();
    }

    // The next scene (menu, connect screen) clears and draws straight to the default framebuffer, so a
    // scene target left bound would swallow its output and show a black window. The viewport goes back
    // too: the scene target set it to its own size.
    private void RestoreWindowIfSceneBound()
    {
        if (!_sceneFramebufferBound)
        {
            return;
        }

        _sceneFramebufferBound = false;
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, Math.Max(_settings.WindowWidth, 1), Math.Max(_settings.WindowHeight, 1));
    }
    private PostChain? _postChain;
    private int _contextSamples = -1;

    // The scene target for this frame's window size, created or recreated as needed. Null when it
    // could not be made complete, which is a supported state and not an error.
    private SceneTarget? EnsureSceneTarget()
    {
        if (_contextSamples < 0)
        {
            // Queried with the default framebuffer bound, which is where Render starts.
            _contextSamples = GL.GetInteger(GetPName.Samples);
        }

        int width = Math.Max(_settings.WindowWidth, 1);
        int height = Math.Max(_settings.WindowHeight, 1);
        if (_sceneTarget != null && _sceneTarget.Matches(width, height, _contextSamples))
        {
            return _sceneTarget.IsAvailable ? _sceneTarget : null;
        }

        _sceneTarget?.Dispose();
        _sceneTarget = new SceneTarget(width, height, _contextSamples);
        return _sceneTarget.IsAvailable ? _sceneTarget : null;
    }

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
        // Bind the offscreen scene buffer before the clear, so the clear colour, the depth buffer
        // and every world pass land in the float target rather than on the window.
        SceneTarget? sceneTarget = _postChain == null ? null : EnsureSceneTarget();
        using SceneFramebufferGuard sceneFramebufferGuard = new(this);
        if (sceneTarget != null)
        {
            _sceneFramebufferBound = true;
            sceneTarget.BindForScene();
        }

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
            (float)args.Time,
            isometric ? CameraPoseForFocus((float)args.Time) : null);
        if (isometric)
        {
            // Fog, the shadow volume, light and prop selection centre on what the camera looks at; the war mist
            // stays on the player (LastCameraFocus), so panning cannot scout through it.
            _render.FogOrigin = _runtime.LastViewTarget;
            _render.LastWorldCamera = presentation.Camera;
            _render.SetIsometricDistance(_runtime.LastCameraPose.Distance);
            _render.ShadowViewForwardGround = _runtime.CurrentGroundForward;
        }
        else
        {
            _render.FogOrigin = null;
            _render.LastWorldCamera = null;
            _render.ShadowViewForwardGround = null;
        }

        _render.LootFeed = _lootFeedback;
        long remoteStatesStart = Stopwatch.GetTimestamp();
        // A live list owned by the presenter; it is only read within this frame.
        IReadOnlyList<RemotePlayerPresentationState> remotePlayerStates = _scenePresenter.BuildRemotePlayerStates(
            RemotePlayerRenderTick);

        UpdateAudioFrame(presentation, remotePlayerStates, sky.StarVisibility, (float)args.Time);
        CollectFrameLights(in presentation, remotePlayerStates);
        // The shadow maps: the sun's map and the casting fires' faces. After the lights are collected (the fire slots are
        // picked from the frame's point lights, so the shadows can never disagree with what is lit) and before DrawWorld
        // uploads the frame lighting and samples the maps. Timed as its own GPU section; BeginSection(World) below ends it.
        gpuTimer.BeginSection(GpuTimerSection.Shadows);
        // SetFrameLighting ran above (PrepareShadows writes the sun matrix into that block and uploads it), and the focus is
        // the one the fog and the props use: the camera's isometric focus, else the camera itself.
        using (GpuDebug.Scope(ShadowsGroup))
        {
            _render.PrepareShadows(presentation.Camera, _render.FogOrigin ?? presentation.Camera.Position, presentation.VisibleWorld);
        }
        long worldDrawStart = Stopwatch.GetTimestamp();
        gpuTimer.BeginSection(GpuTimerSection.World);
        // The boundary mist wall's inputs go up with the frame lighting inside DrawWorld.
        _render.BoundaryMist = new BoundaryMistInput(
            _runtime.Map.Width,
            _runtime.Map.Depth,
            _clientPresentationTimeSeconds,
            _boundaryMistSettings.Enabled);
        GpuDebug.Push(WorldGroup);
        WorldRenderStats worldRenderStats = _render.World.DrawWorld(presentation);
        if (!_readiness.Ready)
        {
            _readiness.ObserveFrame(ModelAssets.PendingCount, worldRenderStats.StaticChunkRebuiltMeshes, args.Time);
            LogWorldReadinessTimeout();
        }
        // Scenery after the terrain and before the actors: the braziers' bowls, the village, the graves. The index
        // follows the placed lights collected above, so each light has its bowl.
        _render.Props.Update(presentation.VisibleWorld, _render.PlacedLights);
        _render.Props.Draw(presentation.Camera, _render.FogOrigin ?? presentation.Camera.Position);
        GpuDebug.Pop();
        // Actors runs from the enemies through the sky, then the Effects section takes over. A
        // section cannot reopen within a frame, so the debug and viewmodel draws that follow are
        // untimed. BeginSection ends the open section first, so the timer queries never nest.
        gpuTimer.BeginSection(GpuTimerSection.Actors);
        GpuDebug.Push(ActorsGroup);
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
        else
        {
            // The figure is not drawn in first person: leaving it must not reuse the last third-person flame point.
            _render.LocalPlayers.ClearHeldFlame();
        }

        long remotePlayerDrawStart = Stopwatch.GetTimestamp();
        RemotePlayerRenderStats remoteRenderStats = _render.RemotePlayers.Draw(
            presentation.Camera,
            remotePlayerStates,
            (float)args.Time);
        double remotePlayerDrawMilliseconds = Stopwatch.GetElapsedTime(remotePlayerDrawStart).TotalMilliseconds;
        LogRemotePlayerRenderDiagnostics(presentation.Camera, remotePlayerStates, remoteRenderStats);
        DrawEconomyEntities(presentation.Camera, (float)args.Time);
        GpuDebug.Pop();

        // Sky pass, still inside the Actors GPU section: after every opaque actor, so it shades only
        // the pixels left at depth 1, and before the debug and viewmodel passes. It restores the
        // world state (DepthFunc Less, depth writes on, blending off) when it is done.
        SkyRenderer skyRenderer = _sky ?? throw new ObjectDisposedException(nameof(PlayScene));
        if (!isometric)
        {
            skyRenderer.Draw(in sky, presentation.Camera.GetViewMatrix(), _render.ProjectionMatrix);
        }

        // Boundary void pass, every camera mode: smoke on the pixels whose view ray reaches the floor
        // plane outside the map. After the sky, so the smoke covers it; it restores the world state.
        GpuDebug.Push(BoundaryVoidGroup);
        if (_boundaryMistSettings.Enabled)
        {
            _boundaryMist?.Draw(presentation.Camera.GetViewMatrix(), _render.ProjectionMatrix);
        }
        GpuDebug.Pop();

        GpuDebug.Push(EffectsGroup);
        DrawEffectsPass(in sky, in presentation, gpuTimer, (float)args.Time);
        GpuDebug.Pop();

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
            GpuShadowMilliseconds = gpuTimer.Latest.ShadowMilliseconds,
            GpuPostMilliseconds = gpuTimer.Latest.PostMilliseconds,
            GpuHudMilliseconds = gpuTimer.Latest.HudMilliseconds,
            GpuPrimitives = gpuTimer.Latest.PrimitivesGenerated,
            DrawCalls = _lastFrameCounters.DrawCalls,
            ProgramBinds = _lastFrameCounters.ProgramBinds,
            UniformUploads = _lastFrameCounters.UniformUploads
        };
        if (_settings.ShowCollisionGeometry)
        {
            long debugDrawStart = Stopwatch.GetTimestamp();
            GpuDebug.Push(DebugOverlaysGroup);
            _render.Debug.DrawCollisionBodies(presentation.MapCollisionBodies, presentation.Camera);
            if (presentation.PlayerCollisionBody != null)
            {
                _render.DrawCollisionBody(presentation.PlayerCollisionBody, presentation.Camera);
            }
            GpuDebug.Pop();
            performance = performance with
            {
                DebugDrawMilliseconds = Stopwatch.GetElapsedTime(debugDrawStart).TotalMilliseconds
            };
        }

        if (_settings.CameraViewMode == CameraViewMode.FirstPerson)
        {
            long viewModelDrawStart = Stopwatch.GetTimestamp();
            using (GpuDebug.Scope(ViewModelGroup))
            {
                _render.ViewModels.Draw(_viewModelController.CurrentFrameState);
            }
            performance = performance with
            {
                ViewModelDrawMilliseconds = Stopwatch.GetElapsedTime(viewModelDrawStart).TotalMilliseconds
            };
        }

        // Resolve the scene onto the window: the multisample blit if there is one, then the bright
        // pass, the blur and the highlight shoulder. It runs AFTER the collision debug draw and the
        // view model, because both of those are world geometry that needs the scene's depth buffer,
        // and BEFORE the HUD, because the HUD is authored in display values and must not be rolled
        // off. Its draws run in their own Post GPU section (opening it closes the Effects one).
        if (sceneTarget != null && _postChain != null)
        {
            gpuTimer.BeginSection(GpuTimerSection.Post);
            sceneTarget.Resolve();
            _postChain.Run(sceneTarget.ResolvedTexture, sceneTarget.Width, sceneTarget.Height);
            _sceneFramebufferBound = false;
        }

        long hudDrawStart = Stopwatch.GetTimestamp();
        gpuTimer.BeginSection(GpuTimerSection.Hud);
        GpuDebug.Push(HudGroup);
        _render.WorldTransferHud = CaptureWorldTransferHudState(_remoteClient);
        _render.HudScene = new HudSceneState(LastReportedPlayerPosition);
        AdvanceHudCaptureScript();
        HudFrameResult hudResult =
            _render.Hud.DrawFrame(
                args,
                windowFocused && !LoadingBlocksInput,
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
        GpuDebug.Pop();
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
