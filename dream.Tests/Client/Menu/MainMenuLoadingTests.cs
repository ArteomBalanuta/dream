using Dream.Assets;
using Dream.Client.Menu;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Menu;
using Dream.Server;
using Dream.Tests.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Menu;

public class MainMenuLoadingTests
{
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
        public ValueTask<RemoteClientSession> ConnectAsync(RemoteClientOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RemoteClientSession(new FakeTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options));
    }

    private sealed class FakeTransport : ITransportConnection
    {
        public string ConnectionId => "menu-loading-test";

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly UiSpriteTable _table;
    private readonly MenuSkin _skin;
    private readonly UiInputRouter _router = new();
    private readonly MenuWidgets _widgets = new();

    public MainMenuLoadingTests()
    {
        Assert.True(UiSpriteTable.TryLoadPages(AssetPaths.UiDirectory, MenuAtlasLoader.Pages, out UiSpriteTable? table, out string error), error);
        _table = table!;
        _skin = MenuSkin.From(_table);
    }

    private static (MainMenuScene Scene, SinglePlayerLauncher Launcher, FakeServer Server) CreateScene()
    {
        FakeServer server = new();
        NetworkSessionCoordinator coordinator = new(server, new FakeConnector(), null);
        SinglePlayerLauncher launcher = new(coordinator, server, null, () => "hero");
        return (new MainMenuScene(launcher, new List<MenuOptionRow>()), launcher, server);
    }

    private InventoryFakeCanvas Draw(MainMenuScene scene)
    {
        InventoryFakeCanvas canvas = new(_table);
        UiLayout layout = UiLayout.Create(1440, 1024, 0f);
        _router.BeginFrame(new UiPointer(new Vector2(-1000f, -1000f), false, false, true));
        _widgets.Begin(canvas, _router, _skin, in layout);
        scene.BuildScreen(_widgets);
        _router.EndFrame();
        return canvas;
    }

    [Fact]
    public void SinglePlayer_ShowsTheLoadingPanelNotTheButtonsWhileBusy()
    {
        var (scene, launcher, _) = CreateScene();
        Assert.Equal(5, Draw(scene).Named(MenuSkin.ButtonNormal).Count());

        scene.Handle(MenuAction.SinglePlayer);

        Assert.True(launcher.IsBusy);
        Assert.False(string.IsNullOrEmpty(scene.LoadingTip));
        InventoryFakeCanvas canvas = Draw(scene);
        Assert.Empty(canvas.Named(MenuSkin.ButtonNormal));
        Assert.True(canvas.HasText(launcher.StatusMessage));
        Assert.True(canvas.HasText(scene.LoadingTip));
    }

    [Fact]
    public void Failure_ReturnsToTheRootWithTheErrorStatus()
    {
        var (scene, launcher, server) = CreateScene();
        scene.Handle(MenuAction.SinglePlayer);
        Draw(scene);
        launcher.NotifyFramePresented();
        launcher.NotifyFramePresented();
        server.ErrorMessage = "port in use";
        scene.Update();

        Assert.True(launcher.Failed);
        Assert.Equal(MenuScreen.Root, scene.Screen);
        InventoryFakeCanvas canvas = Draw(scene);
        Assert.Equal(5, canvas.Named(MenuSkin.ButtonNormal).Count());
        Assert.Contains(canvas.Texts, t => t.Text.Contains("port in use", StringComparison.Ordinal));
    }

    [Fact]
    public void Progress_NeverDecreasesAcrossPhases()
    {
        var (scene, launcher, _) = CreateScene();
        scene.Handle(MenuAction.SinglePlayer);
        float last = 0f;
        for (int i = 0; i < 12 && launcher.Phase != SinglePlayerPhase.Connected; i++)
        {
            Draw(scene);
            float now = LoadingProgress.ForLauncher(launcher.Phase);
            Assert.True(now >= last, $"{launcher.Phase}: {now} < {last}");
            last = now;
            launcher.NotifyFramePresented();
            scene.Update();
        }

        Assert.True(last > 0f);
    }
}
