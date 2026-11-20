using Dream.Application;
using Dream.Client.Menu;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Configuration;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui.Menu;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Menu;

public class MenuFlowTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Launch_ShowsTheMenuByDefault()
    {
        Assert.True(MenuLaunchPolicy.ShouldShowMainMenu(Env()));
    }

    [Theory]
    [InlineData("DREAM_SKIP_MENU", "1")]
    [InlineData("DREAM_SKIP_MENU", "true")]
    [InlineData("DREAM_AUTO_CONNECT", "1")]
    [InlineData("DREAM_AUTO_CONNECT", "yes")]
    [InlineData("DREAM_HUD_CAPTURE", "G:/out/capture.png")]
    public void Launch_SkipsTheMenuForScriptedRuns(string key, string value)
    {
        Assert.False(MenuLaunchPolicy.ShouldShowMainMenu(Env((key, value))));
    }

    [Fact]
    public void Launch_SkipsTheMenuForTheRemoteClient_ButNotForSkipMenuZero()
    {
        Assert.False(MenuLaunchPolicy.ShouldShowMainMenu(Env(), remoteClientRequested: true));
        Assert.True(MenuLaunchPolicy.ShouldShowMainMenu(Env(("DREAM_SKIP_MENU", "0"), ("DREAM_HUD_CAPTURE", " "))));
    }

    [Fact]
    public void ScreenState_NavigatesOptionsKeyBindingsAndCredits()
    {
        MenuScreenState state = new();

        Assert.True(state.Apply(MenuAction.Options));
        Assert.Equal(MenuScreen.Options, state.Current);
        Assert.True(state.Apply(MenuAction.KeyBindings));
        Assert.Equal(MenuScreen.KeyBindings, state.Current);
        Assert.True(state.Apply(MenuAction.Back));
        Assert.Equal(MenuScreen.Options, state.Current);
        Assert.True(state.Apply(MenuAction.Camera));
        Assert.Equal(MenuScreen.Camera, state.Current);
        Assert.True(state.Apply(MenuAction.Back));
        Assert.Equal(MenuScreen.Options, state.Current);
        Assert.True(state.Apply(MenuAction.Back));
        Assert.Equal(MenuScreen.Root, state.Current);
        Assert.False(state.Apply(MenuAction.Camera));
        Assert.True(state.Apply(MenuAction.Credits));
        Assert.Equal(MenuScreen.Credits, state.Current);
        Assert.True(state.Apply(MenuAction.Back));
        Assert.Equal(MenuScreen.Root, state.Current);
        Assert.False(state.Apply(MenuAction.SinglePlayer));
        Assert.False(state.Apply(MenuAction.Back));
        state.Apply(MenuAction.Options);
        state.Reset();
        Assert.Equal(MenuScreen.Root, state.Current);
    }

    private sealed class FakeServer : ILocalServerHandle
    {
        private bool _started;

        public bool IsStarted => _started;

        public bool IsReady => _started && ErrorMessage == null;

        public int BoundPort => IsReady ? 6123 : 0;

        public string? ErrorMessage { get; set; }

        public string? StartingStatus => null;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public LocalServerListenOptions ListenOptions { get; private set; } = LocalServerListenOptions.Loopback;

        // Like the on-demand handle: listen options can change only while no server is running.
        public bool TryConfigureListen(LocalServerListenOptions options)
        {
            if (options != ListenOptions)
            {
                _started = false;
                ListenOptions = options;
            }

            return true;
        }

        public void EnsureStarted() => _started = true;

        public void ResetFailure()
        {
            ErrorMessage = null;
            _started = false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConnector : IRemoteSessionConnector
    {
        public List<RemoteClientOptions> Connects { get; } = [];

        public ValueTask<RemoteClientSession> ConnectAsync(RemoteClientOptions options, CancellationToken cancellationToken = default)
        {
            Connects.Add(options);
            return ValueTask.FromResult(new RemoteClientSession(
                new FakeTransport(),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                options));
        }
    }

    private sealed class FakeTransport : ITransportConnection
    {
        public string ConnectionId => "menu-flow-test";

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (MainMenuScene Scene, SinglePlayerLauncher Launcher, FakeConnector Connector, List<MultiplayerDefaults> Saved) CreateScene(MultiplayerDefaults? defaults = null, IMenuClipboard? clipboard = null)
    {
        FakeServer server = new();
        FakeConnector connector = new();
        NetworkSessionCoordinator coordinator = new(server, connector, null);
        SinglePlayerLauncher launcher = new(coordinator, server, null, () => "hero");
        List<MultiplayerDefaults> saved = [];
        MainMenuScene scene = new(launcher, new List<MenuOptionRow>(), null, defaults, saved.Add, clipboard);
        return (scene, launcher, connector, saved);
    }

    private sealed class FakeClipboard : IMenuClipboard
    {
        public string? Text { get; set; }

        public string? GetText() => Text;

        public bool SetText(string text)
        {
            Text = text;
            return true;
        }
    }

    [Fact]
    public void Scene_SelectAllThenPasteReplacesTheAddress()
    {
        FakeClipboard clipboard = new() { Text = "10.0.0.5" };
        var (scene, _, _, _) = CreateScene(new MultiplayerDefaults("example.org", 7000, 7001, "hero"), clipboard);
        scene.Handle(MenuAction.Multiplayer);

        scene.HandleKey(MenuKey.SelectAll, MenuKeyModifiers.Control);
        scene.HandleKey(MenuKey.Paste, MenuKeyModifiers.Control);

        Assert.Equal("10.0.0.5", scene.Form.Address.Text);
        Assert.False(scene.Form.Address.HasSelection);
    }

    [Fact]
    public void Scene_CopyPutsTheSelectionOnTheClipboard_AndEscapeStillGoesBack()
    {
        FakeClipboard clipboard = new();
        var (scene, _, _, _) = CreateScene(new MultiplayerDefaults("example.org", 7000, 7001, "hero"), clipboard);
        scene.Handle(MenuAction.Multiplayer);

        scene.HandleKey(MenuKey.Home, MenuKeyModifiers.None);
        scene.HandleKey(MenuKey.Right, MenuKeyModifiers.Shift);
        scene.HandleKey(MenuKey.Right, MenuKeyModifiers.Shift);
        scene.HandleKey(MenuKey.Copy, MenuKeyModifiers.Control);

        Assert.Equal("ex", clipboard.Text);
        scene.HandleKey(MenuKey.Escape, MenuKeyModifiers.None);
        Assert.Equal(MenuScreen.Root, scene.Screen);
    }

    [Fact]
    public void ScreenState_NavigatesRootToMultiplayerAndBack()
    {
        MenuScreenState state = new();

        Assert.True(state.Apply(MenuAction.Multiplayer));
        Assert.Equal(MenuScreen.Multiplayer, state.Current);
        Assert.True(state.Apply(MenuAction.Back));
        Assert.Equal(MenuScreen.Root, state.Current);
    }

    [Fact]
    public void Scene_MultiplayerActionOpensTheScreen_AndEscapeGoesBack()
    {
        var (scene, _, _, _) = CreateScene();

        Assert.Equal(MenuAction.None, scene.Handle(MenuAction.Multiplayer));
        Assert.Equal(MenuScreen.Multiplayer, scene.Screen);
        Assert.Equal(MenuAction.None, scene.HandleKey(MenuKey.Escape));
        Assert.Equal(MenuScreen.Root, scene.Screen);
    }

    [Fact]
    public void Scene_EscapeOnRootDoesNothing()
    {
        var (scene, _, _, _) = CreateScene();

        Assert.Equal(MenuAction.None, scene.HandleKey(MenuKey.Escape));
        Assert.Equal(MenuScreen.Root, scene.Screen);
    }

    [Fact]
    public void Scene_EscapeOnOptionsGoesBack()
    {
        var (scene, _, _, _) = CreateScene();

        scene.Handle(MenuAction.Options);
        scene.HandleKey(MenuKey.Escape);

        Assert.Equal(MenuScreen.Root, scene.Screen);
    }

    // Needs WS-A's MultiplayerFormState: typing goes through it.
    [Fact]
    public void Scene_TypedCharactersAndBackspaceEditTheFocusedField()
    {
        var (scene, _, _, _) = CreateScene(new MultiplayerDefaults("host", 7000, 7001, "hero"));
        scene.Handle(MenuAction.Multiplayer);

        scene.QueueTextInput('a');
        scene.QueueTextInput('b');
        scene.HandleKey(MenuKey.Backspace);

        Assert.Equal("hosta", scene.Form.FocusedField!.Text);
    }

    [Fact]
    public void Scene_JoinWithABadPortSetsAnErrorAndDoesNotConnect()
    {
        var (scene, launcher, connector, saved) = CreateScene(new MultiplayerDefaults("example.org", 70000, 7001, "hero"));
        scene.Handle(MenuAction.Multiplayer);

        scene.Handle(MenuAction.Join);

        Assert.False(launcher.IsBusy);
        Assert.Empty(connector.Connects);
        Assert.Empty(saved);
        Assert.Equal(MenuScreen.Multiplayer, scene.Screen);
    }

    [Fact]
    public void Scene_JoinValid_PersistsTheTypedValuesAndConnects()
    {
        var (scene, launcher, _, saved) = CreateScene(new MultiplayerDefaults(" example.org ", 7000, 7001, " hero "));
        scene.Handle(MenuAction.Multiplayer);

        scene.Handle(MenuAction.Join);

        MultiplayerDefaults persisted = Assert.Single(saved);
        Assert.Equal(new MultiplayerDefaults("example.org", 7000, 7001, "hero"), persisted);
        Assert.Equal(SinglePlayerPhase.Connecting, launcher.Phase);
    }

    [Fact]
    public void Scene_HostValid_PersistsTheHostPortAndWakes()
    {
        var (scene, launcher, _, saved) = CreateScene(new MultiplayerDefaults("example.org", 7000, 7001, "hero"));
        scene.Handle(MenuAction.Multiplayer);

        scene.Handle(MenuAction.Host);

        Assert.Equal(7001, Assert.Single(saved).HostPort);
        Assert.Equal(SinglePlayerPhase.Waking, launcher.Phase);
    }

    [Fact]
    public void Scene_BackIsIgnoredWhileBusy()
    {
        var (scene, launcher, _, _) = CreateScene(new MultiplayerDefaults("example.org", 7000, 7001, "hero"));
        scene.Handle(MenuAction.Multiplayer);
        scene.Handle(MenuAction.Host);
        Assert.True(launcher.IsBusy);

        scene.Handle(MenuAction.Back);
        scene.HandleKey(MenuKey.Escape);

        Assert.Equal(MenuScreen.Multiplayer, scene.Screen);
    }

    [Fact]
    public void StandardRows_AreWiredToSettingsAndConfig()
    {
        GameSettings settings = new(new Vector2i(1440, 1024));
        DesktopClientConfig config = new();
        int capApplied = 0;
        int persisted = 0;
        List<MenuOptionRow> rows = MenuOptionRows.CreateStandard(settings, config, () => capApplied++, () => persisted++);

        MenuOptionRow scale = rows.Single(r => r.Label == "UI Scale");
        MenuOptionRow fps = rows.Single(r => r.Label == "Max FPS");
        MenuOptionRow damage = rows.Single(r => r.Label == "Damage Text Size");
        MenuOptionRow admin = rows.Single(r => r.Label.StartsWith("Admin HUD", StringComparison.Ordinal));

        scale.SetValue!(2.5f);
        Assert.Equal(2.5f, settings.UiScaleSetting);
        Assert.Equal(2.5f, config.UiScale);
        scale.SetValue!(0.75f);
        Assert.Equal(0f, settings.UiScaleSetting);
        Assert.Equal("Auto", scale.FormatValue(scale.GetValue!()));
        scale.Commit!();
        Assert.Equal(1, persisted);

        fps.SetValue!(MenuOptionRows.FrameCaps.Length - 1);
        Assert.Equal(FrameRateCap.Uncapped, config.MaxFramesPerSecond);
        Assert.Equal("uncapped", fps.FormatValue(fps.GetValue!()));
        fps.SetValue!(1f);
        Assert.Equal(60, config.MaxFramesPerSecond);
        Assert.Equal(2, capApplied);

        MenuOptionRow vsync = rows.Single(r => r.Label == "VSync");
        Assert.Equal(2f, vsync.GetValue!());
        Assert.Equal("Adaptive", vsync.FormatValue(2f));
        vsync.SetValue!(0f);
        Assert.Equal(OpenTK.Windowing.Common.VSyncMode.Off, config.VSync);
        Assert.Equal("Off", vsync.FormatValue(0f));
        vsync.SetValue!(1f);
        Assert.Equal(OpenTK.Windowing.Common.VSyncMode.On, config.VSync);
        Assert.Equal(4, capApplied);

        damage.SetValue!(1.5f);
        Assert.Equal(1.5f, settings.FloatingCombatText.SizeMultiplier);
        Assert.Equal(1.5f, config.FloatingCombatText.SizeMultiplier);
        Assert.Equal("1.50x", damage.FormatValue(1.5f));

        admin.SetToggle!(true);
        Assert.True(settings.AdminHudAvailable);
        Assert.True(config.AdminHud);
        admin.SetToggle!(false);
        Assert.False(config.AdminHud);
    }

    [Theory]
    [InlineData(144, 4)]
    [InlineData(0, 6)]
    [InlineData(30, 0)]
    [InlineData(100, 2)]
    [InlineData(500, 5)]
    public void FrameCapIndex_PicksTheNearestStep(int cap, int expected)
    {
        Assert.Equal(expected, (int)MenuOptionRows.FrameCapIndex(cap));
    }
}
