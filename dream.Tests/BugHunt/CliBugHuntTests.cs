using Dream.Client.Input;
using Dream.Client.Menu;
using Dream.Client.Networking;
using Dream.Client.Scenes;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Rendering.Ui.Menu;
using Dream.Rendering.Ui.Panels;
using Dream.Server;
using Xunit;

namespace Dream.Tests.BugHunt;

// Regression tests for the BUGHUNTER-CLI packet (BH-CLI-1, BH-CLI-2, BH-CLI-3). Headless: no window
// and no GL context is created, only the state machines and queues the findings are about.
public class CliBugHuntTests
{
    // ---- BH-CLI-1: hosting must not predict dev toggles the hosted server strips ----

    [Fact]
    public void BhCli1_HostedIntegratedServerWithDevCommandsOff_DoesNotPredictNoclipOrFreeze()
    {
        // Host on port 6123: the client joins its own server over loopback, but IntegratedLocalServerHandle
        // left dev commands at the dedicated server's environment value (off), so PlayerCommandHandler
        // strips noclip/freeze and a client that predicted them is snapped back on every snapshot.
        FakeLocalServer hosted = new(isReady: true, boundPort: 6123, devCommandsAllowed: false)
        {
            Listen = LocalServerListenOptions.Hosted(6123)
        };

        Assert.False(PlayScene.ShouldAllowDevToggles(hosted, "127.0.0.1", 6123, devCommandsEnvironmentValue: null));
    }

    [Fact]
    public void BhCli1_SinglePlayerIntegratedServer_StillPredictsDevToggles()
    {
        FakeLocalServer single = new(isReady: true, boundPort: 6123, devCommandsAllowed: true);

        Assert.True(PlayScene.ShouldAllowDevToggles(single, "127.0.0.1", 6123, devCommandsEnvironmentValue: null));
    }

    [Fact]
    public void BhCli1_HostedWithDreamDevCommands_PredictsDevTogglesAgain()
    {
        // DREAM_DEV_COMMANDS=1 is what turns them on for a hosted server, and the client follows.
        FakeLocalServer hosted = new(isReady: true, boundPort: 6123, devCommandsAllowed: true)
        {
            Listen = LocalServerListenOptions.Hosted(6123)
        };

        Assert.True(PlayScene.ShouldAllowDevToggles(hosted, "127.0.0.1", 6123, devCommandsEnvironmentValue: "1"));
    }

    [Fact]
    public void BhCli1_RemoteServer_NeverPredictsDevToggles()
    {
        FakeLocalServer local = new(isReady: true, boundPort: 6123, devCommandsAllowed: true);

        Assert.False(PlayScene.ShouldAllowDevToggles(local, "192.168.1.25", 5057, devCommandsEnvironmentValue: null));
        Assert.False(PlayScene.ShouldAllowDevToggles(null, "192.168.1.25", 5057, devCommandsEnvironmentValue: null));
    }

    [Fact]
    public void BhCli1_OnDemandHandle_ReportsTheStartedServersDevCommandFlag()
    {
        FakeLocalServer started = new(isReady: true, boundPort: 6123, devCommandsAllowed: false);
        OnDemandLocalServerHandle handle = new(_ => started);

        Assert.False(handle.DevCommandsAllowed);
        handle.EnsureStarted();
        Assert.False(handle.DevCommandsAllowed);
    }

    // ---- BH-CLI-2: a Join started from the title screen must be cancellable ----

    [Fact]
    public async Task BhCli2_EscapeCancelsAJoinThatIsStillConnecting()
    {
        BlockingConnector connector = new();
        FakeLocalServer server = new(isReady: false, boundPort: 0, devCommandsAllowed: false);
        NetworkSessionCoordinator coordinator = new(server, connector, null);
        SinglePlayerLauncher launcher = new(coordinator, server, null, () => "hero");
        MainMenuScene scene = new(launcher, new List<MenuOptionRow>());
        scene.Handle(MenuAction.Multiplayer);
        scene.Handle(MenuAction.Join);
        Assert.True(launcher.IsBusy);

        scene.HandleKey(MenuKey.Escape);

        Assert.False(launcher.IsBusy);
        Assert.Equal(SinglePlayerPhase.Idle, launcher.Phase);
        Assert.Equal(MenuScreen.Multiplayer, scene.Screen);

        // The connect the player walked away from must not drag them into the world when it lands.
        connector.Complete();
        await coordinator.WaitForPendingConnectionAsync();
        Assert.False(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
        Assert.Null(session);
        Assert.NotEqual(ConnectionFlowState.Connected, coordinator.State);
        Assert.False(launcher.TryTakeConnectedSession(out _));
        Assert.Equal(SinglePlayerPhase.Idle, launcher.Phase);
    }

    [Fact]
    public async Task BhCli2_ACancelledJoinThatFailsLaterLeavesTheScreenIdle()
    {
        BlockingConnector connector = new();
        FakeLocalServer server = new(isReady: false, boundPort: 0, devCommandsAllowed: false);
        NetworkSessionCoordinator coordinator = new(server, connector, null);
        SinglePlayerLauncher launcher = new(coordinator, server, null, () => "hero");

        launcher.RequestJoin(new ClientConnectionOptions("192.168.1.25", "5057", "hero"));
        Assert.True(launcher.CancelConnect());

        connector.Fail(new IOException("Connection timed out."));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.NotEqual(ConnectionFlowState.Failed, coordinator.State);
        launcher.Update();
        Assert.Equal(SinglePlayerPhase.Idle, launcher.Phase);
    }

    [Fact]
    public void BhCli2_CancelDoesNotTouchASinglePlayerOrHostStart()
    {
        BlockingConnector connector = new();
        FakeLocalServer server = new(isReady: false, boundPort: 0, devCommandsAllowed: false);
        NetworkSessionCoordinator coordinator = new(server, connector, null);
        SinglePlayerLauncher launcher = new(coordinator, server, null, () => "hero");

        launcher.RequestStart();

        Assert.False(launcher.CancelConnect());
        Assert.True(launcher.IsBusy);
        Assert.Equal(SinglePlayerPhase.Waking, launcher.Phase);
    }

    // ---- BH-CLI-3: the chat text-input queue must not grow while the line is closed ----

    [Fact]
    public void BhCli3_CharactersTypedWhileTheChatLineIsClosedAreNotQueued()
    {
        // The loading screen holds the scene's input gate (and so ChatInput.Update, the only drain)
        // for as long as the "press any key" prompt is up, while every typed character still arrives.
        ChatInput chat = new(new ChatHudState());

        for (int i = 0; i < 10_000; i++)
        {
            chat.QueueTextInput('a');
        }

        Assert.Equal(0, chat.PendingCharacterCount);
    }

    [Fact]
    public void BhCli3_CharactersTypedWhileTheChatLineIsOpenStillReachIt()
    {
        ChatInput chat = new(new ChatHudState());
        chat.Open();

        chat.QueueTextInput('h');
        chat.QueueTextInput('i');

        Assert.Equal(2, chat.PendingCharacterCount);
    }

    // ---- fakes ----

    private sealed class FakeLocalServer(bool isReady, int boundPort, bool devCommandsAllowed) : ILocalServerHandle
    {
        public bool IsReady { get; } = isReady;

        public int BoundPort { get; } = boundPort;

        public string? ErrorMessage => null;

        public bool DevCommandsAllowed { get; } = devCommandsAllowed;

        public LocalServerListenOptions Listen { get; set; } = LocalServerListenOptions.Loopback;

        public LocalServerListenOptions ListenOptions => Listen;

        public bool TryConfigureListen(LocalServerListenOptions options)
        {
            Listen = options;
            return true;
        }

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A connect that never settles until the test says so.
    private sealed class BlockingConnector : IRemoteSessionConnector
    {
        private readonly TaskCompletionSource<RemoteClientSession> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RemoteClientSession> ConnectAsync(
            RemoteClientOptions options,
            CancellationToken cancellationToken = default)
        {
            RemoteClientSession session = await _result.Task;
            return session;
        }

        public void Complete() =>
            _result.TrySetResult(new RemoteClientSession(
                new DeadTransport(),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                new RemoteClientOptions("192.168.1.25", 5057, "bughunt-cli", "hero", "hero")));

        public void Fail(Exception error) => _result.TrySetException(error);
    }

    private sealed class DeadTransport : ITransportConnection
    {
        public string ConnectionId => "bughunt-cli";

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
