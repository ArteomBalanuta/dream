using Dream.Assets;
using Dream.Client.Networking;
using Dream.Client.Scenes;
using Dream.Client.Startup;
using Dream.Configuration;
using Dream.Rendering.Core;
using Dream.Rendering.ViewModels;
using Dream.Simulation.Players;
using Dream.Simulation.World;

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
    private readonly ILocalServerHandle _localServer;
    private StartupConnectionScene? _startupScene;
    private PlayScene? _playScene;

    private double _lastTime;
    private int _frameCount;
    public int FPS { get; private set; }

    public Game() : base(GameWindowSettings.Default, new NativeWindowSettings
    {
        ClientSize = new Vector2i(1440, 1024),
        Title = GameWindowTitleBuilder.BaseTitle
    })
    {
        ViewModelRigSettings viewModelRig = ViewModelRigTextConfig.LoadOrCreate(
            AssetPaths.Config("viewmodel-rig.txt"));
        _desktopClientConfigPath = AssetPaths.Config("desktop-client.txt");
        _desktopClientConfig = DesktopClientTextConfig.LoadOrCreate(_desktopClientConfigPath);
        _settings = new GameSettings(new Vector2i(1440, 1024), viewModelRig, _desktopClientConfig);
        _localServer = IntegratedLocalServerHandle.StartAsync().AsTask().GetAwaiter().GetResult();
        NetworkSessionCoordinator coordinator = new(_localServer, new RemoteSessionConnector());
        ClientConnectionOptions? initialOptions = null;
        bool autoConnectUsesInitialOptions = false;
        if (RemoteClientOptions.TryResolveFromEnvironment(out RemoteClientOptions? remoteOptions))
        {
            initialOptions = new ClientConnectionOptions(
                remoteOptions!.Host,
                remoteOptions.Port.ToString(),
                remoteOptions.DisplayName);
            autoConnectUsesInitialOptions = true;
        }
        else
        {
            initialOptions = coordinator.ResolveInitialOptions(_desktopClientConfig.ToConnectionOptions());
        }

        _startupScene = new StartupConnectionScene(
            new Vector2i(1440, 1024),
            coordinator,
            initialOptions,
            PersistConnectionOptions,
            autoConnectUsesInitialOptions);
        CursorState = _startupScene.DesiredCursorState;
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
                    _desktopClientConfig,
                    PersistDesktopClientConfig,
                    PlayScene.ResolveLoopbackRemotePlayersEnabled(),
                    remoteClient);
                _startupScene.Dispose();
                _startupScene = null;
            }
        }
        else
        {
            _playScene.Update(args, KeyboardState, MouseState);
            Console.Title = $"Player Position: {_playScene.LastReportedPlayerPosition}";
        }

        CursorState = _playScene?.DesiredCursorState ?? _startupScene!.DesiredCursorState;
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        if (_playScene == null)
        {
            _startupScene!.Render(args, IsFocused, MouseState, KeyboardState);
        }
        else
        {
            _playScene.Render(args, IsFocused, MouseState, size => ClientSize = size);
        }
        SwapBuffers();

        UpdateFPS();
        this.Title = GameWindowTitleBuilder.Build(FPS, _playScene?.ConnectedServerEndpoint);
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

    protected override void OnUnload()
    {
        base.OnUnload();
        _playScene?.Dispose();
        _startupScene?.Dispose();
        _localServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
