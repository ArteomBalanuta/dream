using System.Runtime.ExceptionServices;
using Dream.Assets;
using Dream.Client.Audio;
using Dream.Client.Identity;
using Dream.Client.Menu;
using Dream.Client.Networking;
using Dream.Client.Scenes;
using Dream.Client.Startup;
using Dream.Configuration;
using Dream.Rendering.Core;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Gl;
using Dream.Rendering.Ui.Menu;
using Dream.Rendering.ViewModels;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Dream.Application;

﻿// Game.cs
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using MouseButton = OpenTK.Windowing.GraphicsLibraryFramework.MouseButton;

public class Game : GameWindow
{
    private readonly GameSettings _settings;
    private readonly DesktopClientConfig _desktopClientConfig;
    private readonly string _desktopClientConfigPath;
    // Replaced with a fresh, unstarted handle when a play session ends: the ended session's
    // integrated server is stopped with it, and the next local session starts a new one.
    private ILocalServerHandle _localServer;
    private string? _contentError;
    // The stops of the integrated servers let go of so far. They all share one world database, so
    // a new integrated server starts only after them, and the window close waits for them.
    private readonly LocalServerShutdownGate _localServerShutdown = new();
    // OnUnload is called by GameWindow.Run only after the loop exits normally. An exception out of a
    // frame callback leaves Run before that, so Dispose backstops the local-server stop; this flag
    // keeps the two paths from both running it.
    private bool _unloaded;
    // Title setters are synchronous Win32 calls; skip them when the text has not changed.
    private readonly ChangedTextSink _consoleTitle = new(text => Console.Title = text);
    private readonly ChangedTextSink _windowTitle;
    private StartupConnectionScene? _startupScene;
    private PlayScene? _playScene;

    // The title screen and the Esc pause menu share one renderer, created on the first frame that
    // needs it (it needs the GL context). When it cannot be created the old connection screen and
    // the ImGui settings window are used instead.
    private MainMenuScene? _mainMenu;
    private MenuUiRenderer? _menuRenderer;
    private bool _menuRendererFailed;
    private readonly bool _menuEnabled;
    private readonly List<MenuOptionRow> _optionRows;
    private readonly MenuScreenState _pauseScreen = new();
    private bool _pauseWasVisible;
    private bool _exitToMainMenuPending;
    // The tip the title menu's loading screen showed, kept for the same screen over the world.
    private string? _loadingTip;
    // DREAM_MENU_CAPTURE: walks the title screen and writes a PNG of each screen (art review).
    private readonly MenuCapture? _menuCapture = MenuFlowScript.IsEnabled(Environment.GetEnvironmentVariable) ? null : MenuCapture.FromEnvironment();
    // DREAM_MENU_FLOW: the scripted Single Player / pause / exit walk (verification only).
    private readonly MenuFlowScript? _menuFlow = MenuFlowScript.IsEnabled(Environment.GetEnvironmentVariable) ? new MenuFlowScript() : null;

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

    // The sample count is a window-creation hint, so the config must be read before the base constructor
    // runs. LoadOrCreate writes the file when missing, so the result is cached and the constructor body
    // reuses it instead of reading a second time.
    private static DesktopClientConfig? _startupConfig;
    private static string? _startupConfigPath;

    private static DesktopClientConfig LoadStartupConfig()
    {
        _startupConfigPath ??= DataPaths.DesktopClientConfig;
        return _startupConfig ??= DesktopClientTextConfig.LoadOrCreate(_startupConfigPath);
    }

    private static int ResolveStartupSamples() =>
        MsaaSettings.ResolveFromEnvironment(LoadStartupConfig().MsaaSamples);

    private static NativeWindowSettings CreateWindowSettings(Version apiVersion) => new()
    {
        APIVersion = apiVersion,
        Profile = ContextProfile.Core,
        NumberOfSamples = ResolveStartupSamples(),
        ClientSize = new Vector2i(1440, 1024),
        Title = GameWindowTitleBuilder.BaseTitle,
        // A capture run drives itself and must not pull focus from whatever the user is doing.
        StartFocused = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DREAM_HUD_CAPTURE"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MenuCapture.EnvironmentVariable)),
#if DEBUG
        // KHR_debug messages are only guaranteed in a debug context.
        Flags = ContextFlags.Debug,
#endif
    };

    // Frames presented so far; RenderDoc's capture-frame option counts them.
    private long _renderedFrames;

    public Game(Version apiVersion) : base(GameWindowSettings.Default, CreateWindowSettings(apiVersion))
    {
        GLUtils.BindRenderThread();
        // Before any shader is compiled, so every program gets its debug label.
        GpuDebug.Initialize();
        Console.WriteLine(
            $"[startup] gl: requested {GlContextVersion.Describe(apiVersion)} core, "
            + $"got {GL.GetString(StringName.Version)} / {GL.GetString(StringName.Renderer)}");
        _windowTitle = new ChangedTextSink(text => Title = text);
        ViewModelRigSettings viewModelRig = ViewModelRigTextConfig.LoadOrCreate(
            AssetPaths.Config("viewmodel-rig.txt"));
        _desktopClientConfig = LoadStartupConfig();
        _desktopClientConfigPath = _startupConfigPath!;
        _settings = new GameSettings(new Vector2i(1440, 1024), viewModelRig, _desktopClientConfig);
        // The single-threaded GameWindow runs update and render together off UpdateFrequency (0 = uncapped).
        // The swap interval paces the frame (Adaptive by default); the cap backstops a driver that
        // ignores it and keeps an unsynced client from spinning at four-digit FPS.
        ApplyFramePacing("[startup]");
        Console.WriteLine(
            $"[startup] msaa: requested {MsaaSettings.Describe(MsaaSettings.ResolveFromEnvironment(_desktopClientConfig.MsaaSamples))}, "
            + $"granted {GL.GetInteger(GetPName.Samples)} samples");
        // Silent (never throws) with no device, DREAM_AUDIO=0, the capture harness or Sound off. It also
        // routes the classic UI router's click/hover feedback, so the menus sound too.
        GameAudio.EnsureStarted(AudioVolumes.FromConfig(_desktopClientConfig));
        _localServer = CreateLocalServerHandle();
        _optionRows = BuildOptionRows();
        bool remoteClient = RemoteClientOptions.TryResolveFromEnvironment(out RemoteClientOptions? remoteOptions);
        _menuEnabled = MenuLaunchPolicy.ShouldShowMainMenu(Environment.GetEnvironmentVariable, remoteClient);
        // A client that only joins a remote server never passes ServerBootstrap's content check, so
        // the content is read once here; a failure is shown on the connection screen.
        _contentError = ClientContentCheck.TryReadShipped();
        if (_contentError != null)
        {
            Console.Error.WriteLine($"[startup] {_contentError}");
        }

        if (remoteClient)
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
        else if (_menuEnabled)
        {
            // The title screen. No server starts until Single Player is chosen.
            ShowMainMenu(sessionEndReason: null);
        }
        else
        {
            // DREAM_SKIP_MENU, DREAM_AUTO_CONNECT and the HUD capture keep the connection screen,
            // which starts the integrated server at launch.
            ShowStartupScene(
                new Vector2i(1440, 1024),
                _desktopClientConfig.ToConnectionOptions(),
                resolveLocalPort: true,
                autoConnectUsesInitialOptions: false,
                sessionEndReason: null);
        }
    }

    // The rows of the Options screen: the standard ones, then whatever AddExtraOptionRows adds.
    private List<MenuOptionRow> BuildOptionRows()
    {
        List<MenuOptionRow> rows = MenuOptionRows.CreateStandard(
            _settings,
            _desktopClientConfig,
            ApplyFrameCap,
            PersistDesktopClientConfig);
        AddExtraOptionRows(rows);
        return rows;
    }

    // Hook for features that add their own Options rows (the audio sliders).
    private void AddExtraOptionRows(List<MenuOptionRow> rows)
    {
        DesktopClientConfig cfg = _desktopClientConfig;
        rows.Add(MenuOptionRow.Header("Audio"));
        rows.Add(MenuOptionRow.Toggle(
            "Sound",
            () => cfg.AudioEnabled,
            value =>
            {
                cfg.AudioEnabled = value;
                if (value)
                {
                    // Sound that was off at launch starts its device now; a fresh mixer has no music yet
                    // (SetMusic is a no-op when the track is already playing).
                    GameAudio.EnsureStarted(AudioVolumes.FromConfig(cfg));
                    GameAudio.Current.SetMusic(_playScene != null ? MusicTrack.World : MusicTrack.Menu);
                }
                else
                {
                    GameAudio.Current.SetVolumes(AudioVolumes.FromConfig(cfg));
                }
            },
            PersistDesktopClientConfig));
        rows.Add(VolumeRow("Master", () => cfg.VolumeMaster, value => cfg.VolumeMaster = value));
        rows.Add(VolumeRow("Effects", () => cfg.VolumeSfx, value => cfg.VolumeSfx = value));
        rows.Add(VolumeRow("Music", () => cfg.VolumeMusic, value => cfg.VolumeMusic = value));
        rows.Add(VolumeRow("Interface", () => cfg.VolumeUi, value => cfg.VolumeUi = value));
    }

    private MenuOptionRow VolumeRow(string label, Func<float> get, Action<float> write) =>
        MenuOptionRow.Slider(
            label,
            0f,
            1f,
            0.05f,
            get,
            value =>
            {
                write(value);
                GameAudio.Current.SetVolumes(AudioVolumes.FromConfig(_desktopClientConfig));
            },
            value => $"{MathF.Round(value * 100f):0}%",
            PersistDesktopClientConfig);

    private void ApplyFrameCap() => ApplyFramePacing("[settings]");

    // Applies the swap interval and the sleep-pacing frequency from the config and the environment.
    private void ApplyFramePacing(string logPrefix)
    {
        bool captureRun = VSyncSettings.IsCaptureRun(Environment.GetEnvironmentVariable);
        VSyncMode mode = VSyncSettings.ResolveFromEnvironment(_desktopClientConfig.VSync);
        // OpenTK's own Adaptive mode toggles the interval per frame from IsRunningSlowly, which turns
        // vsync off whenever the sleep cap sits above the refresh rate. So OpenTK's VSync stays Off
        // (nothing toggles the interval) and the swap interval is set directly, after the setter.
        VSync = VSyncMode.Off;
        GLFW.SwapInterval(VSyncSettings.SwapInterval(mode, AdaptiveSyncSupported()));
        int cap = FrameRateCap.ResolveFromEnvironment(_desktopClientConfig.MaxFramesPerSecond);
        int frequency = FrameRateCap.ResolveUpdateFrequency(cap, mode, ReadRefreshRate());
        UpdateFrequency = frequency;
        Console.WriteLine(
            $"{logPrefix} vsync: {VSyncSettings.Describe(mode)}{(captureRun ? " (capture run)" : string.Empty)}, "
            + $"frame cap: {FrameRateCap.Describe(cap)}, update frequency: {FrameRateCap.Describe(frequency)}");
    }

    private static bool AdaptiveSyncSupported()
    {
        try
        {
            return GLFW.ExtensionSupported("WGL_EXT_swap_control_tear")
                || GLFW.ExtensionSupported("GLX_EXT_swap_control_tear");
        }
        catch (Exception)
        {
            return false;
        }
    }

    private int ReadRefreshRate()
    {
        try
        {
            return Math.Max(0, Monitors.GetMonitorFromWindow(this).CurrentVideoMode.RefreshRate);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // The title screen over a fresh coordinator on the current local server handle. sessionEndReason
    // is shown under the buttons when a play session just ended.
    private void ShowMainMenu(string? sessionEndReason)
    {
        // A start that will wait on the loading screen's prompt asks the server to keep the player's spawn
        // protection until their first input.
        NetworkSessionCoordinator coordinator = new(
            _localServer,
            new RemoteSessionConnector(),
            _contentError,
            ClientIdentityStore.ForCurrentUser())
        {
            HoldSpawnProtection = LoadingScreenPolicy.Resolve(true, Environment.GetEnvironmentVariable) == LoadingGateMode.WaitForKey
        };
        SinglePlayerLauncher launcher = new(coordinator, _localServer, _contentError, () => _desktopClientConfig.PlayerName);
        _mainMenu = new MainMenuScene(
            launcher,
            _optionRows,
            sessionEndReason,
            new MultiplayerDefaults(
                _desktopClientConfig.Host,
                _desktopClientConfig.Port,
                _desktopClientConfig.HostPort,
                _desktopClientConfig.PlayerName),
            PersistMultiplayer,
            SystemClipboard);
        CursorState = _mainMenu.DesiredCursorState;
        // After any ended play scene's teardown (which set None), so the title screen keeps its music.
        GameAudio.Current.SetMusic(MusicTrack.Menu);
    }

    // The handle reads the shutdown gate when it is asked to start, not now: the ended session's
    // server is stopped (and tracked) only after its replacement handle exists.
    private ILocalServerHandle CreateLocalServerHandle()
    {
        // listen: Single Player keeps loopback on a free port; Host on the Multiplayer screen binds every
        // interface on the chosen port.
        return new OnDemandLocalServerHandle(
            listen => IntegratedLocalServerHandle.StartAsync(
                hostFactory: () => ServerBootstrap.CreateAuthoritativeHost(
                    new ServerStartOptions(
                        NewWorldSize: _settings.FloorSize,
                        MaintenanceWait: DatabaseMaintenanceLock.IntegratedStartWait)),
                listen: listen).AsTask().GetAwaiter().GetResult(),
            startAfter: () => _localServerShutdown.Pending);
    }

    // The connection screen, at launch and after a play session ends. Each one gets its own
    // coordinator over the current local server handle. At launch (resolveLocalPort) a loopback
    // endpoint is resolved to the integrated server's current port, which starts it. After a
    // session to the integrated server ended (relaunchLocalServerFor), a connect to that ended
    // endpoint starts the new integrated server instead (LocalServerRelaunchConnector).
    private void ShowStartupScene(
        Vector2i size,
        ClientConnectionOptions options,
        bool resolveLocalPort,
        bool autoConnectUsesInitialOptions,
        string? sessionEndReason,
        RemoteClientOptions? relaunchLocalServerFor = null,
        bool allowBack = false)
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

        NetworkSessionCoordinator coordinator = new(
            _localServer,
            connector,
            _contentError,
            ClientIdentityStore.ForCurrentUser());
        ClientConnectionOptions initialOptions = resolveLocalPort
            ? coordinator.ResolveInitialOptions(options)
            : options;
        _startupScene = new StartupConnectionScene(
            size,
            coordinator,
            initialOptions,
            PersistConnectionOptions,
            autoConnectUsesInitialOptions,
            sessionEndReason,
            allowBack);
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
            UpdateFrontEnd();
        }
        else
        {
            if (_exitToMainMenuPending)
            {
                // Chosen in the pause menu during the last render: leave the scene here, outside any draw.
                _exitToMainMenuPending = false;
                EndPlaySession(reason: null);
            }
            else
            {
                _playScene.Update(args, KeyboardState, MouseState);
                Vector3 position = _playScene.LastReportedPlayerPosition;
                _consoleTitle.Set($"Player Position: ({position.X:0.0}, {position.Y:0.0}, {position.Z:0.0})");
                ReturnToStartupIfSessionEnded();
            }
        }

        CursorState = _playScene?.DesiredCursorState
            ?? _mainMenu?.DesiredCursorState
            ?? _startupScene?.DesiredCursorState
            ?? CursorState.Normal;
    }

    // The title screen or the connection screen: advance it and enter the world when a session connects.
    private void UpdateFrontEnd()
    {
        if (_mainMenu != null)
        {
            _mainMenu.Update();
            if (_mainMenu.TryTakeConnectedSession(out RemoteClientSession? menuSession) && menuSession != null)
            {
                _loadingTip = _mainMenu.LoadingTip;
                _mainMenu = null;
                EnterPlayScene(menuSession, fromMenu: true);
            }

            return;
        }

        if (_startupScene == null)
        {
            return;
        }

        _startupScene.Update();
        if (_startupScene.TryTakeConnectedSession(out RemoteClientSession? remoteClient) && remoteClient != null)
        {
            EnterPlayScene(remoteClient, fromMenu: false);
            // The replaced startup scene releases its menu renderer and ImGui context here;
            // the play scene's Render has already created and made current its own context.
            _startupScene.Dispose();
            _startupScene = null;
        }
        else if (_startupScene.TryTakeBackRequest())
        {
            _startupScene.Dispose();
            _startupScene = null;
            ShowMainMenu(sessionEndReason: null);
        }
    }

    private void EnterPlayScene(RemoteClientSession remoteClient, bool fromMenu)
    {
        _settings.ResetOverlays();
        _pauseScreen.Reset();
        _pauseWasVisible = false;
        _playScene = new PlayScene(
            _settings,
            remoteClient,
            _desktopClientConfig,
            PersistDesktopClientConfig,
            PlayScene.ResolveLoopbackRemotePlayersEnabled(),
            _localServer);
        // From the title menu the loading screen carries on over the world until the player presses a key.
        _playScene.BeginLoadingScreen(LoadingScreenPolicy.Resolve(fromMenu, Environment.GetEnvironmentVariable));
        // Esc inside a pause-menu sub-screen (Options, Key Bindings) goes back one screen instead of
        // closing the whole menu; on the pause root it closes the menu as before.
        _playScene.TryEscapeBack = () => _settings.PauseMenuVisible && _pauseScreen.Apply(MenuAction.Back);
        // Ctrl+V into the chat line, which is also where /dev commands are typed. The same clipboard the
        // menu's text fields use, so there is one seam rather than two.
        _playScene.Clipboard = SystemClipboard;
        // The viewport and settings resolution follow a window that was resized while a menu was up.
        _playScene.Resize(ClientSize);
        // The classic inventory's static panel state belongs to one scene.
        Dream.Rendering.Ui.Panels.InventoryUiState.Reset();
    }

    // A play session that ended (the server closed or went silent, or the join failed) would
    // otherwise leave the player in a frozen world.
    private void ReturnToStartupIfSessionEnded()
    {
        if (_playScene == null || !PlaySessionEndTransition.HasEnded(_playScene, out string? reason))
        {
            return;
        }

        EndPlaySession(reason);
    }

    // Leaves the play scene: a dropped session (reason) or Exit to Main Menu (null). Runs on the update
    // pass of the single-threaded window, so the GL context is current for the play scene's GL cleanup.
    // The scene is cleared before its teardown and the local server handle replaced first, so
    // OnUnload can never dispose either a second time. The title screen comes back (or, when the menu
    // was skipped at launch, the connection screen with the same endpoint and name) and shows the
    // reason; the player can start a local game again or join a server.
    private void EndPlaySession(string? reason)
    {
        if (_playScene == null)
        {
            return;
        }

        PlayScene endedScene = _playScene;
        _playScene = null;
        _pauseWasVisible = false;
        _settings.ResetOverlays();
        Dream.Rendering.Ui.Panels.InventoryUiState.Reset();
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

        // The scene's GL objects are gone; stop the context naming them as bound.
        GLUtils.UnbindAll();

        bool toMainMenu = _menuEnabled && !_menuRendererFailed;
        Console.WriteLine(
            $"[session] returned to {(toMainMenu ? "the main menu" : "the connection screen")}: {reason ?? "left by the player"}" +
            (stoppedLocalServer ? " (local server stopped)" : string.Empty));
        if (toMainMenu)
        {
            ShowMainMenu(reason);
            return;
        }

        // The endpoint comes back exactly as it was and nothing starts here: no integrated server
        // on the update thread, no rebound port, no redirect of a loopback dedicated server that
        // dropped. Only when the ended session was this game's own integrated server does a
        // connect to that endpoint start a new one, lazily, on its fresh port.
        ShowStartupScene(
            ClientSize,
            PlaySessionEndTransition.ReturnOptions(endedOptions),
            resolveLocalPort: false,
            autoConnectUsesInitialOptions: false,
            sessionEndReason: reason ?? "left the game",
            relaunchLocalServerFor: relaunchFor);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        Stopwatch frameStopwatch = Stopwatch.StartNew();
        if (_playScene != null)
        {
            _playScene.Render(args, IsFocused, MouseState, size => ClientSize = size);
            RenderLoadingOverlay();
            RenderPauseMenu();
        }
        else if (_mainMenu != null)
        {
            RenderMainMenu();
        }
        else
        {
            _startupScene?.Render(args, IsFocused, MouseState, KeyboardState);
        }
        RunMenuFlowStep();
        Stopwatch swapStopwatch = Stopwatch.StartNew();
        SwapBuffers();
        swapStopwatch.Stop();
        RenderDocHook.OnFrameEnd(_renderedFrames++);
        frameStopwatch.Stop();
        _playScene?.ReportPresentedFramePerformance(
            frameStopwatch.Elapsed.TotalMilliseconds,
            swapStopwatch.Elapsed.TotalMilliseconds);

        CheckGLErrorsAfterFrame();

        UpdateFPS();
        _windowTitle.Set(GameWindowTitleBuilder.Build(FPS, _playScene?.ConnectedServerEndpoint));
    }

    private void RunMenuFlowStep()
    {
        if (_menuFlow == null)
        {
            return;
        }

        MenuFlowScript.Command command = _menuFlow.Tick(new MenuFlowScript.SceneState(
            _playScene != null,
            _settings.PauseMenuVisible,
            _pauseScreen.Current == MenuScreen.Root,
            _playScene?.LoadingOverlayVisible ?? false,
            _playScene?.LoadingAwaitingKey ?? false));
        if (command.Step == MenuFlowScript.Step.None && command.CapturePng == null)
        {
            return;
        }

        // A capture-only command (Step.None with a PNG) is saved here too, then does nothing else.
        Console.WriteLine($"[flow] {command.Step}: {command.Log}");
        string? dir = Environment.GetEnvironmentVariable(MenuCapture.EnvironmentVariable);
        if (command.CapturePng != null && !string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir.Trim());
            UiFramebufferCapture.CaptureBackBufferToPng(Path.Combine(dir.Trim(), command.CapturePng), ClientSize.X, ClientSize.Y);
        }

        switch (command.Step)
        {
            case MenuFlowScript.Step.PressAnyKey:
                _playScene?.PressContinue();
                break;
            case MenuFlowScript.Step.StartSinglePlayer:
                _mainMenu?.Handle(MenuAction.SinglePlayer);
                break;
            case MenuFlowScript.Step.OpenPauseWithEsc:
            case MenuFlowScript.Step.EscBack:
                _playScene?.PressEscape();
                break;
            case MenuFlowScript.Step.OpenOptionsInPause:
                ApplyPauseAction(MenuAction.Options);
                break;
            case MenuFlowScript.Step.ExitToMainMenu:
                ApplyPauseAction(MenuAction.ExitToMainMenu);
                break;
            case MenuFlowScript.Step.ExitGame:
            case MenuFlowScript.Step.Fail:
                Close();
                break;
        }
    }

    // The shared front-end renderer, created on first use. Null (and latched) when it cannot be built.
    private MenuUiRenderer? EnsureMenuRenderer()
    {
        if (_menuRenderer != null || _menuRendererFailed)
        {
            return _menuRenderer;
        }

        _menuRenderer = MenuUiRenderer.TryCreate(out string error);
        if (_menuRenderer == null)
        {
            _menuRendererFailed = true;
            Console.Error.WriteLine($"[menu] the menu UI is unavailable: {error}");
        }

        return _menuRenderer;
    }

    private UiPointer MenuPointer() =>
        _menuCapture?.ScriptedPointer(ClientSize.X, ClientSize.Y, _settings.UiScaleSetting)
        ?? new UiPointer(
            new Vector2(MouseState.Position.X, MouseState.Position.Y),
            MouseState.IsButtonDown(MouseButton.Left),
            MouseState.IsButtonDown(MouseButton.Right),
            IsFocused);

    private void RenderMainMenu()
    {
        // Only the play scene sets the viewport, so a resized window must be followed here.
        GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
        MenuUiRenderer? renderer = EnsureMenuRenderer();
        if (renderer == null || _mainMenu == null)
        {
            // No menu art or glyphs: fall back to the connection screen, which starts the local server.
            _mainMenu = null;
            ShowStartupScene(
                ClientSize,
                _desktopClientConfig.ToConnectionOptions(),
                resolveLocalPort: true,
                autoConnectUsesInitialOptions: false,
                sessionEndReason: null);
            return;
        }

        MenuAction action = _mainMenu.Render(renderer, ClientSize.X, ClientSize.Y, _settings.UiScaleSetting, MenuPointer());
        if (renderer.State == UiAtlasState.Failed)
        {
            Console.Error.WriteLine($"[menu] the menu art failed to load ({renderer.FailureReason}); using the connection screen.");
            _menuRendererFailed = true;
            _mainMenu = null;
            ShowStartupScene(
                ClientSize,
                _desktopClientConfig.ToConnectionOptions(),
                resolveLocalPort: true,
                autoConnectUsesInitialOptions: false,
                sessionEndReason: null);
            return;
        }

        if (_menuCapture != null)
        {
            _menuCapture.AfterFrame(
                _mainMenu,
                renderer,
                ClientSize.X,
                ClientSize.Y,
                _settings.UiScaleSetting,
                path => UiFramebufferCapture.CaptureBackBufferToPng(path, ClientSize.X, ClientSize.Y));
            if (_menuCapture.Done)
            {
                Close();
            }
        }

        switch (action)
        {
            case MenuAction.Exit:
                Close();
                break;
        }
    }

    // The loading screen over the world's first frames: opaque, so the world is never seen half-loaded, and
    // left up until the player presses a key. Without the menu UI the player goes straight in.
    private void RenderLoadingOverlay()
    {
        if (_playScene == null || !_playScene.LoadingOverlayVisible)
        {
            return;
        }

        MenuUiRenderer? renderer = EnsureMenuRenderer();
        if (renderer == null || renderer.State == UiAtlasState.Failed)
        {
            _playScene.SkipLoadingScreen();
            return;
        }

        GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
        _loadingState = new LoadingScreenState(
            _playScene.LoadingStage,
            LoadingProgress.ForScene(_playScene.LoadingProgress01),
            _playScene.LoadingAwaitingKey,
            _loadingTip,
            (float)GLFW.GetTime());
        renderer.Draw(
            ClientSize.X,
            ClientSize.Y,
            _settings.UiScaleSetting,
            new UiPointer(new Vector2(-1000f, -1000f), false, false, IsFocused),
            _loadingBuild);
    }

    private LoadingScreenState _loadingState;
    private Action<MenuWidgets>? _loadingBuildCache;

    private Action<MenuWidgets> _loadingBuild => _loadingBuildCache ??= w => LoadingScreenPanel.Build(w, in _loadingState);

    // The Esc menu, drawn over the finished world frame. Resume closes it; Exit to Main Menu is
    // carried out on the next update, not inside this draw.
    private void RenderPauseMenu()
    {
        if (!_settings.PauseMenuVisible)
        {
            _pauseWasVisible = false;
            return;
        }

        if (!_pauseWasVisible)
        {
            _pauseWasVisible = true;
            _pauseScreen.Reset();
        }

        MenuUiRenderer? renderer = EnsureMenuRenderer();
        if (renderer == null || renderer.State == UiAtlasState.Failed)
        {
            // Without the menu UI, Esc opens the ImGui developer window as it did before.
            _settings.ClosePauseMenu();
            _settings.ToggleDeveloperSettings();
            return;
        }

        MenuAction action = MenuAction.None;
        renderer.Draw(ClientSize.X, ClientSize.Y, _settings.UiScaleSetting, MenuPointer(), w => action = BuildPauseScreen(w));
        ApplyPauseAction(action);
    }

    private MenuAction BuildPauseScreen(MenuWidgets w) => _pauseScreen.Current switch
    {
        MenuScreen.Options => OptionsPanel.Build(w, _optionRows, backdrop: false),
        MenuScreen.KeyBindings => KeyBindingsPanel.Build(w, backdrop: false),
        _ => PauseMenuPanel.Build(w, _settings.AdminHudAvailable)
    };

    private void ApplyPauseAction(MenuAction action)
    {
        if (action == MenuAction.None || _pauseScreen.Apply(action))
        {
            return;
        }

        switch (action)
        {
            case MenuAction.Resume:
                _settings.ClosePauseMenu();
                break;
            case MenuAction.DeveloperSettings:
                _settings.ToggleDeveloperSettings();
                break;
            case MenuAction.ExitToMainMenu:
                _settings.ClosePauseMenu();
                _exitToMainMenuPending = true;
                break;
            case MenuAction.ExitGame:
                Close();
                break;
        }
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
        _playScene?.QueueTextInput((uint)e.Unicode);
        _mainMenu?.QueueTextInput((uint)e.Unicode);
    }

    // The title menu's keys: editing and Tab/Enter on the Multiplayer screen, Esc to go back a screen.
    // The play scene reads its own keys from KeyboardState; _mainMenu and _playScene are never both set.
    protected override void OnKeyDown(KeyboardKeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_mainMenu == null)
        {
            return;
        }

        MenuKey? key = MapMenuKey(e.Key, e.Control, e.Alt, e.IsRepeat);
        MenuKeyModifiers modifiers = (e.Shift ? MenuKeyModifiers.Shift : MenuKeyModifiers.None)
            | (e.Control && !e.Alt ? MenuKeyModifiers.Control : MenuKeyModifiers.None);
        if (key is { } menuKey && _mainMenu.HandleKey(menuKey, modifiers) == MenuAction.Exit)
        {
            Close();
        }
    }

    // Editing keys repeat while held; the one-shot keys and the Ctrl shortcuts do not (a held Ctrl+V
    // would paste over and over). AltGr arrives as Ctrl+Alt on Windows, so Alt cancels the shortcuts. The shortcuts follow the key's position name, as most games do.
    internal static MenuKey? MapMenuKey(Keys key, bool control, bool alt, bool isRepeat) => key switch
    {
        Keys.Backspace => MenuKey.Backspace,
        Keys.Delete => MenuKey.Delete,
        Keys.Left => MenuKey.Left,
        Keys.Right => MenuKey.Right,
        Keys.Home => MenuKey.Home,
        Keys.End => MenuKey.End,
        _ when isRepeat => null,
        Keys.Tab => MenuKey.Tab,
        Keys.Enter or Keys.KeyPadEnter => MenuKey.Enter,
        Keys.Escape => MenuKey.Escape,
        Keys.A when control && !alt => MenuKey.SelectAll,
        Keys.C when control && !alt => MenuKey.Copy,
        Keys.X when control && !alt => MenuKey.Cut,
        Keys.V when control && !alt => MenuKey.Paste,
        _ => null
    };

    // One clipboard for the window, shared by the menu's fields and the chat line. Created on first use
    // because the window must exist before GLFW can be asked for it.
    private IMenuClipboard? _systemClipboard;

    private IMenuClipboard SystemClipboard => _systemClipboard ??= new WindowClipboard(this);

    // The system clipboard through GLFW. A clipboard that holds no text (or that GLFW cannot read) is empty.
    //
    // It is no longer "[menu]": the chat line pastes through this same instance, so a Ctrl+V at the chat
    // prompt was reporting itself as a menu error.
    private sealed class WindowClipboard(Game window) : IMenuClipboard
    {
        // GLFW THROWS when the clipboard simply holds no text - on Windows that surfaces as "Element not
        // found" - and that is not a failure: this interface's contract is that no text means null. The
        // only thing separating it from a real fault is the exception message, which is localised and a
        // Win32 implementation detail, so matching on it would be a guess that breaks on a non-English
        // machine.
        //
        // So the message is not classified, it is DEDUPLICATED: each distinct failure is reported once.
        // The first occurrence keeps all of its diagnostic value, and holding Ctrl+V over an empty
        // clipboard stops writing a line to stderr per keypress (it wrote seven in one session).
        private string? _reportedFailure;

        public string? GetText()
        {
            try
            {
                return window.ClipboardString;
            }
            catch (Exception ex)
            {
                if (!string.Equals(_reportedFailure, ex.Message, StringComparison.Ordinal))
                {
                    _reportedFailure = ex.Message;
                    Console.Error.WriteLine(
                        $"[clipboard] read returned no text ({ex.Message}); further identical reads stay silent");
                }

                return null;
            }
        }

        public bool SetText(string text)
        {
            try
            {
                window.ClipboardString = text;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[clipboard] write failed: {ex.Message}");
                return false;
            }
        }
    }

    // The scenes own GL objects (Render, the shared model cache, the startup menu's ImGui context),
    // so they are disposed first, on the render thread while the window's GL context is still
    // current, and before base.OnUnload. The local server goes last, after the play scene has
    // closed its connection to it. A scene that throws still lets the rest of the teardown run.
    protected override void OnUnload()
    {
        Console.WriteLine("[exit] window closing: releasing scenes and stopping the local server");
        _unloaded = true;
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
                try
                {
                    _startupScene?.Dispose();
                    _startupScene = null;
                    _mainMenu = null;
                }
                finally
                {
                    try
                    {
                        _menuRenderer?.Dispose();
                        _menuRenderer = null;
                    }
                    finally
                    {
                        // Last of the client teardown: the scenes have released their sounds and loops.
                        // Never throws; also clears the UI router's click/hover feedback.
                        GameAudio.Shutdown();
                    }
                }
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

        Console.WriteLine(failures is null ? "[exit] shutdown complete" : $"[exit] shutdown finished with {failures.Count} failure(s)");
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

    // Join or Host on the Multiplayer screen: the typed address, ports and name are kept for next time.
    private void PersistMultiplayer(MultiplayerDefaults values)
    {
        _desktopClientConfig.Host = values.Address;
        _desktopClientConfig.Port = values.JoinPort;
        _desktopClientConfig.HostPort = values.HostPort;
        _desktopClientConfig.PlayerName = values.PlayerName;
        PersistDesktopClientConfig();
    }

    // These commit callbacks fire from inside the render pass (the Options rows are drawn there), so a
    // read-only install directory, or a file held open by a backup or AV scanner, must not take the
    // session down: the failure is reported and the setting simply does not persist.
    private void PersistDesktopClientConfig()
    {
        DesktopClientTextConfig.TrySave(_desktopClientConfigPath, _desktopClientConfig);
    }

    // Backstop for the path OnUnload never sees: GameWindow.Run has no exception handling around the
    // frame callbacks, so an exception out of a frame leaves Run without OnUnload ever being called, and
    // GameWindow.Dispose does not call it either. Program.RunDesktop's `using` still reaches here, so the
    // integrated server is stopped and the world drained to disk before the process goes down.
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_unloaded)
        {
            _unloaded = true;
            Console.Error.WriteLine("[exit] window torn down without a clean unload: stopping the local server");
            if (PlaySessionEndTransition.StopLocalServerForExit(_localServer, _localServerShutdown) is Exception stopFailure)
            {
                Console.Error.WriteLine($"[exit] {stopFailure.GetType().Name}: {stopFailure.Message}");
            }
        }

        base.Dispose(disposing);
    }
}
