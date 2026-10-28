using System.Runtime.ExceptionServices;
using Dream.Assets;
using Dream.Client.Networking;
using Dream.Client.Scenes;
using Dream.Client.Startup;
using Dream.Configuration;
using Dream.Rendering.Core;
using Dream.Rendering.ViewModels;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Dream.Application;

﻿// Game.cs
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

public class Game : GameWindow
{
    private readonly GameSettings _settings;
    private readonly DesktopClientConfig _desktopClientConfig;
    private readonly string _desktopClientConfigPath;
    // Replaced with a fresh, unstarted handle when a play session ends: the ended session's
    // integrated server is stopped with it, and the next local session starts a new one.
    private ILocalServerHandle _localServer;
    // The stops of the integrated servers let go of so far. They all share one world database, so
    // a new integrated server starts only after them, and the window close waits for them.
    private readonly LocalServerShutdownGate _localServerShutdown = new();
    // Title setters are synchronous Win32 calls; skip them when the text has not changed.
    private readonly ChangedTextSink _consoleTitle = new(text => Console.Title = text);
    private readonly ChangedTextSink _windowTitle;
    private StartupConnectionScene? _startupScene;
    private PlayScene? _playScene;

    // DEBUG drains the GL error flags after every frame. Release builds check less often, which is
    // enough to notice GL_OUT_OF_MEMORY or GL_CONTEXT_LOST and close instead of drawing garbage.
#if DEBUG
    internal const int GLErrorCheckIntervalFrames = 1;
#else
    internal const int GLErrorCheckIntervalFrames = 60;
#endif
    private int _framesSinceGLErrorCheck;
    private bool _closingForGLFault;

    private double _lastTime;
    private int _frameCount;
    public int FPS { get; private set; }

    public Game() : base(GameWindowSettings.Default, new NativeWindowSettings
    {
        ClientSize = new Vector2i(1440, 1024),
        Title = GameWindowTitleBuilder.BaseTitle,
#if DEBUG
        // KHR_debug messages are only guaranteed in a debug context.
        Flags = ContextFlags.Debug,
#endif
    })
    {
        GLUtils.BindRenderThread();
        _windowTitle = new ChangedTextSink(text => Title = text);
        ViewModelRigSettings viewModelRig = ViewModelRigTextConfig.LoadOrCreate(
            AssetPaths.Config("viewmodel-rig.txt"));
        _desktopClientConfigPath = AssetPaths.Config("desktop-client.txt");
        _desktopClientConfig = DesktopClientTextConfig.LoadOrCreate(_desktopClientConfigPath);
        _settings = new GameSettings(new Vector2i(1440, 1024), viewModelRig, _desktopClientConfig);
        // The single-threaded GameWindow runs update and render together off UpdateFrequency (0 = uncapped).
        // VSync stays off; the cap keeps a focused client from spinning at four-digit FPS.
        int frameCap = FrameRateCap.ResolveFromEnvironment(_desktopClientConfig.MaxFramesPerSecond);
        UpdateFrequency = frameCap;
        Console.WriteLine($"[startup] frame cap: {FrameRateCap.Describe(frameCap)}");
        _localServer = CreateLocalServerHandle();
        if (RemoteClientOptions.TryResolveFromEnvironment(out RemoteClientOptions? remoteOptions))
        {
            ShowStartupScene(
                new Vector2i(1440, 1024),
                new ClientConnectionOptions(
                    remoteOptions!.Host,
                    remoteOptions.Port.ToString(),
                    remoteOptions.DisplayName),
                resolveLocalPort: false,
                autoConnectUsesInitialOptions: true,
                sessionEndReason: null);
        }
        else
        {
            ShowStartupScene(
                new Vector2i(1440, 1024),
                _desktopClientConfig.ToConnectionOptions(),
                resolveLocalPort: true,
                autoConnectUsesInitialOptions: false,
                sessionEndReason: null);
        }
    }

    // The handle reads the shutdown gate when it is asked to start, not now: the ended session's
    // server is stopped (and tracked) only after its replacement handle exists.
    private ILocalServerHandle CreateLocalServerHandle()
    {
        return new OnDemandLocalServerHandle(
            () => IntegratedLocalServerHandle.StartAsync().AsTask().GetAwaiter().GetResult(),
            startAfter: () => _localServerShutdown.Pending);
    }

    // The connection screen, at launch and after a play session ends. Each one gets its own
    // coordinator over the current local server handle. At launch (resolveLocalPort) a loopback
    // endpoint is resolved to the integrated server's current port, which starts it. After a
    // session to the integrated server ended (relaunchLocalServerFor), a connect to that ended
    // endpoint starts the new integrated server instead (LocalServerRelaunchConnector).
    [MemberNotNull(nameof(_startupScene))]
    private void ShowStartupScene(
        Vector2i size,
        ClientConnectionOptions options,
        bool resolveLocalPort,
        bool autoConnectUsesInitialOptions,
        string? sessionEndReason,
        RemoteClientOptions? relaunchLocalServerFor = null)
    {
        IRemoteSessionConnector connector = new RemoteSessionConnector();
        if (relaunchLocalServerFor != null)
        {
            connector = new LocalServerRelaunchConnector(
                connector,
                _localServer,
                relaunchLocalServerFor.Host,
                relaunchLocalServerFor.Port);
        }

        NetworkSessionCoordinator coordinator = new(_localServer, connector);
        ClientConnectionOptions initialOptions = resolveLocalPort
            ? coordinator.ResolveInitialOptions(options)
            : options;
        _startupScene = new StartupConnectionScene(
            size,
            coordinator,
            initialOptions,
            PersistConnectionOptions,
            autoConnectUsesInitialOptions,
            sessionEndReason);
        CursorState = _startupScene.DesiredCursorState;
    }

    protected override void OnLoad()
    {
        base.OnLoad();
#if DEBUG
        bool installed = GLUtils.InstallDebugOutput(RenderFaultLog.Shared);
        Console.WriteLine(installed
            ? "[startup] GL debug output: high and medium severity"
            : "[startup] GL debug output: unavailable (no KHR_debug)");
#endif
    }

    public void UpdateFPS()
    {
        double currentTime = GLFW.GetTime(); // Get current time in seconds
        _frameCount++;

        if (currentTime - _lastTime >= 1.0) // Every second
        {
            FPS = _frameCount;
            _frameCount = 0;
            _lastTime = currentTime;
        }
    }



    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        if (_playScene == null)
        {
            _startupScene!.Update();
            if (_startupScene.TryTakeConnectedSession(out RemoteClientSession? remoteClient) && remoteClient != null)
            {
                _playScene = new PlayScene(
                    _settings,
                    remoteClient,
                    _desktopClientConfig,
                    PersistDesktopClientConfig,
                    PlayScene.ResolveLoopbackRemotePlayersEnabled(),
                    _localServer);
                // The replaced startup scene releases its menu renderer and ImGui context here;
                // the play scene's Render has already created and made current its own context.
                _startupScene.Dispose();
                _startupScene = null;
            }
        }
        else
        {
            _playScene.Update(args, KeyboardState, MouseState);
            Vector3 position = _playScene.LastReportedPlayerPosition;
            _consoleTitle.Set($"Player Position: ({position.X:0.0}, {position.Y:0.0}, {position.Z:0.0})");
            ReturnToStartupIfSessionEnded();
        }

        CursorState = _playScene?.DesiredCursorState ?? _startupScene!.DesiredCursorState;
    }

    // A play session that ended (the server closed or went silent, or the join failed) would
    // otherwise leave the player in a frozen world. Runs on the update pass of the single-threaded
    // window, so the GL context is current for the play scene's GL cleanup. The scene is cleared
    // before its teardown and the local server handle replaced first, so OnUnload can never
    // dispose either a second time. The connection screen comes back with the same endpoint and
    // name and shows the reason; the player can reconnect or start a local game again.
    private void ReturnToStartupIfSessionEnded()
    {
        if (_playScene == null || !PlaySessionEndTransition.HasEnded(_playScene, out string? reason))
        {
            return;
        }

        PlayScene endedScene = _playScene;
        _playScene = null;
        ILocalServerHandle endedLocalServer = _localServer;
        _localServer = CreateLocalServerHandle();
        RemoteClientOptions endedOptions = endedScene.SessionOptions;
        bool stoppedLocalServer = endedLocalServer.IsStarted;
        // Decided before the teardown: was the ended session on this integrated server at all?
        RemoteClientOptions? relaunchFor = PlaySessionEndTransition.RelaunchTarget(endedLocalServer, endedOptions);

        // A stop that outlasts the teardown's wait is tracked in _localServerShutdown, and the
        // next integrated server starts only after it.
        IReadOnlyList<Exception> failures =
            PlaySessionEndTransition.TearDown(endedScene, endedLocalServer, shutdownGate: _localServerShutdown);
        foreach (Exception failure in failures)
        {
            Console.WriteLine($"[session] teardown failed: {failure.GetType().Name}: {failure.Message}");
        }

        Console.WriteLine(
            $"[session] returned to the connection screen: {reason}" +
            (stoppedLocalServer ? " (local server stopped)" : string.Empty));
        // The endpoint comes back exactly as it was and nothing starts here: no integrated server
        // on the update thread, no rebound port, no redirect of a loopback dedicated server that
        // dropped. Only when the ended session was this game's own integrated server does a
        // connect to that endpoint start a new one, lazily, on its fresh port.
        ShowStartupScene(
            ClientSize,
            PlaySessionEndTransition.ReturnOptions(endedOptions),
            resolveLocalPort: false,
            autoConnectUsesInitialOptions: false,
            sessionEndReason: reason,
            relaunchLocalServerFor: relaunchFor);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        Stopwatch frameStopwatch = Stopwatch.StartNew();
        if (_playScene == null)
        {
            _startupScene!.Render(args, IsFocused, MouseState, KeyboardState);
        }
        else
        {
            _playScene.Render(args, IsFocused, MouseState, size => ClientSize = size);
        }
        Stopwatch swapStopwatch = Stopwatch.StartNew();
        SwapBuffers();
        swapStopwatch.Stop();
        frameStopwatch.Stop();
        _playScene?.ReportPresentedFramePerformance(
            frameStopwatch.Elapsed.TotalMilliseconds,
            swapStopwatch.Elapsed.TotalMilliseconds);

        CheckGLErrorsAfterFrame();

        UpdateFPS();
        _windowTitle.Set(GameWindowTitleBuilder.Build(FPS, _playScene?.ConnectedServerEndpoint));
    }

    private void CheckGLErrorsAfterFrame()
    {
        if (_closingForGLFault || ++_framesSinceGLErrorCheck < GLErrorCheckIntervalFrames)
        {
            return;
        }

        _framesSinceGLErrorCheck = 0;
        if (GLUtils.CheckGLError("frame end") != GLFaultKind.Fatal)
        {
            return;
        }

        // Out of memory or a lost context leaves GL state undefined. The error is already in the
        // fault log; close through the normal path so OnUnload still releases the scenes.
        _closingForGLFault = true;
        RenderFaultLog.Shared.Report(
            "gl-fatal",
            new InvalidOperationException("Closing the window after a fatal OpenGL error (out of memory or context lost)."));
        Close();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        Vector2i size = new(e.Width, e.Height);
        _startupScene?.Resize(size);
        _playScene?.Resize(size);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        _startupScene?.QueueTextInput((uint)e.Unicode);
    }

    // The scenes own GL objects (Render, the shared model cache, the startup menu's ImGui context),
    // so they are disposed first, on the render thread while the window's GL context is still
    // current, and before base.OnUnload. The local server goes last, after the play scene has
    // closed its connection to it. A scene that throws still lets the rest of the teardown run.
    protected override void OnUnload()
    {
        List<Exception>? failures = null;
        try
        {
            try
            {
                _playScene?.Dispose();
                _playScene = null;
            }
            finally
            {
                _startupScene?.Dispose();
                _startupScene = null;
            }
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        try
        {
            base.OnUnload();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }
        finally
        {
            // The window is closing, so this waits longer than the session-end teardown: up to
            // PlaySessionEndTransition.LocalServerExitTimeout (the persistence drain deadline plus
            // busy_timeout) for this server and any earlier one still saving, logging progress, so
            // the final checkpoint is not cut off by the process exit. Only a server hung past
            // that is abandoned.
            if (PlaySessionEndTransition.StopLocalServerForExit(_localServer, _localServerShutdown) is Exception stopFailure)
            {
                (failures ??= []).Add(stopFailure);
            }
        }

        if (failures is not null)
        {
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            throw new AggregateException(failures);
        }
    }

    private void PersistConnectionOptions(ClientConnectionOptions options)
    {
        _desktopClientConfig.ApplyConnectionOptions(options);
        PersistDesktopClientConfig();
    }

    private void PersistDesktopClientConfig()
    {
        DesktopClientTextConfig.Save(_desktopClientConfigPath, _desktopClientConfig);
    }
}
