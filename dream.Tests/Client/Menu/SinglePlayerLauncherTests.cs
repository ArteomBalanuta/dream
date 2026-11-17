using Dream.Client.Identity;
using Dream.Client.Menu;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Xunit;

namespace Dream.Tests.Client.Menu;

public class SinglePlayerLauncherTests
{
    private sealed class FakeServer : ILocalServerHandle
    {
        private bool _started;
        private int _pollsUntilReady;

        public int StartCalls { get; private set; }

        public int ResetCalls { get; private set; }

        public bool IsStarted => _started;

        public bool IsReady => _started && _pollsUntilReady <= 0 && ErrorMessage == null;

        public int BoundPort => IsReady ? 6123 : 0;

        public string? ErrorMessage { get; set; }

        // Ready after this many reads of IsReady following the start.
        public int ReadsBeforeReady { get; set; }

        public string? StartingStatus => _started && !IsReady ? "Starting local server..." : null;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public void EnsureStarted()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            StartCalls++;
            ListenAtStart = ListenOptions;
            _pollsUntilReady = ReadsBeforeReady;
        }

        public LocalServerListenOptions ListenOptions { get; private set; } = LocalServerListenOptions.Loopback;

        public List<LocalServerListenOptions> ConfigureCalls { get; } = [];

        public bool AllowConfigure { get; set; } = true;

        // The listen options at the moment EnsureStarted first ran.
        public LocalServerListenOptions? ListenAtStart { get; private set; }

        public bool TryConfigureListen(LocalServerListenOptions options)
        {
            ConfigureCalls.Add(options);
            if (!AllowConfigure)
            {
                return false;
            }

            if (options != ListenOptions)
            {
                // Like the on-demand handle: a server on other options is detached.
                _started = false;
                ErrorMessage = null;
            }

            ListenOptions = options;
            return true;
        }

        public void ResetFailure()
        {
            ResetCalls++;
            ErrorMessage = null;
            _started = false;
        }

        // IsReady is polled once per update; this counts the polls down.
        public void Tick() => _pollsUntilReady--;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConnector : IRemoteSessionConnector
    {
        public List<RemoteClientOptions> Connects { get; } = [];

        public bool FailConnect { get; set; }

        public ValueTask<RemoteClientSession> ConnectAsync(RemoteClientOptions options, CancellationToken cancellationToken = default)
        {
            Connects.Add(options);
            if (FailConnect)
            {
                throw new InvalidOperationException("connection refused");
            }

            return ValueTask.FromResult(new RemoteClientSession(
                new FakeTransport(),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                options));
        }
    }

    private sealed class FakeTransport : ITransportConnection
    {
        public string ConnectionId => "launcher-test";

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (SinglePlayerLauncher Launcher, FakeServer Server, FakeConnector Connector, NetworkSessionCoordinator Coordinator) Create(string? contentError = null)
    {
        FakeServer server = new();
        FakeConnector connector = new();
        NetworkSessionCoordinator coordinator = new(server, connector, contentError);
        return (new SinglePlayerLauncher(coordinator, server, contentError, () => "hero"), server, connector, coordinator);
    }

    private static void PresentFrames(SinglePlayerLauncher launcher, int count)
    {
        for (int i = 0; i < count; i++)
        {
            launcher.NotifyFramePresented();
        }
    }

    [Fact]
    public void NothingStartsUntilRequested_AndNotBeforeAFrameWasPresented()
    {
        var (launcher, server, _, _) = Create();

        launcher.Update();
        Assert.Equal(SinglePlayerPhase.Idle, launcher.Phase);
        Assert.Equal(0, server.StartCalls);

        launcher.RequestStart();
        Assert.Equal(SinglePlayerPhase.Waking, launcher.Phase);
        Assert.True(launcher.IsBusy);
        Assert.Equal(SinglePlayerLauncher.WakingMessage, launcher.StatusMessage);

        launcher.Update();
        Assert.Equal(0, server.StartCalls);

        PresentFrames(launcher, 2);
        launcher.Update();
        Assert.Equal(1, server.StartCalls);
    }

    [Fact]
    public async Task ReadyServer_ConnectsToItsPortAndHandsOverTheSession()
    {
        var (launcher, server, connector, coordinator) = Create();
        launcher.RequestStart();
        PresentFrames(launcher, 2);

        launcher.Update();
        Assert.Equal(SinglePlayerPhase.Connecting, launcher.Phase);
        await coordinator.WaitForPendingConnectionAsync();
        launcher.Update();

        Assert.True(launcher.TryTakeConnectedSession(out RemoteClientSession? session));
        Assert.NotNull(session);
        Assert.Equal(SinglePlayerPhase.Connected, launcher.Phase);
        RemoteClientOptions connect = Assert.Single(connector.Connects);
        Assert.Equal("127.0.0.1", connect.Host);
        Assert.Equal(6123, connect.Port);
        Assert.Equal("hero", connect.DisplayName);
        Assert.False(launcher.TryTakeConnectedSession(out _));
        await session!.DisposeAsync();
        Assert.Equal(1, server.StartCalls);
    }

    [Fact]
    public async Task SlowServer_WaitsInStartingThenConnects()
    {
        var (launcher, server, connector, coordinator) = Create();
        server.ReadsBeforeReady = 2;
        launcher.RequestStart();
        PresentFrames(launcher, 2);

        launcher.Update();
        Assert.Equal(SinglePlayerPhase.Starting, launcher.Phase);
        Assert.Empty(connector.Connects);

        server.Tick();
        server.Tick();
        launcher.Update();
        Assert.Equal(SinglePlayerPhase.Connecting, launcher.Phase);
        await coordinator.WaitForPendingConnectionAsync();
        launcher.Update();
        Assert.True(launcher.TryTakeConnectedSession(out RemoteClientSession? session));
        await session!.DisposeAsync();
    }

    [Fact]
    public void ServerError_FailsWithItsMessage_AndARetryResetsTheFailure()
    {
        var (launcher, server, _, _) = Create();
        launcher.RequestStart();
        PresentFrames(launcher, 2);
        server.ErrorMessage = "port in use";
        launcher.Update();

        Assert.Equal(SinglePlayerPhase.Failed, launcher.Phase);
        Assert.True(launcher.Failed);
        Assert.False(launcher.IsBusy);
        Assert.Contains("port in use", launcher.StatusMessage);

        // The failed attempt put the error on the handle; a retry clears it first.
        server.ErrorMessage = "port in use";
        launcher.RequestStart();
        Assert.Equal(1, server.ResetCalls);
        Assert.Equal(SinglePlayerPhase.Waking, launcher.Phase);
    }

    [Fact]
    public void ContentError_FailsAtOnceWithoutStartingAServer()
    {
        var (launcher, server, _, _) = Create(contentError: "items.json is missing");

        launcher.RequestStart();

        Assert.Equal(SinglePlayerPhase.Failed, launcher.Phase);
        Assert.Equal("items.json is missing", launcher.StatusMessage);
        PresentFrames(launcher, 3);
        launcher.Update();
        Assert.Equal(0, server.StartCalls);
    }

    [Fact]
    public async Task RequestHost_ConfiguresHostedBeforeStart_AndConnectsWithTheGivenName()
    {
        var (launcher, server, connector, coordinator) = Create();
        launcher.RequestHost(7777, "host-a");
        Assert.Equal(LocalServerListenOptions.Hosted(7777), Assert.Single(server.ConfigureCalls));
        Assert.Equal(0, server.StartCalls);
        PresentFrames(launcher, 2);

        launcher.Update();
        Assert.Equal(LocalServerListenOptions.Hosted(7777), server.ListenAtStart);
        Assert.Equal(SinglePlayerPhase.Connecting, launcher.Phase);
        await coordinator.WaitForPendingConnectionAsync();
        launcher.Update();

        Assert.True(launcher.TryTakeConnectedSession(out RemoteClientSession? session));
        Assert.Equal("host-a", Assert.Single(connector.Connects).DisplayName);
        await session!.DisposeAsync();
    }

    [Fact]
    public void RequestStart_ConfiguresLoopback()
    {
        var (launcher, server, _, _) = Create();
        launcher.RequestStart();
        Assert.Equal(LocalServerListenOptions.Loopback, Assert.Single(server.ConfigureCalls));
    }

    [Fact]
    public void RequestHost_WhenConfigureIsRefused_Fails()
    {
        var (launcher, server, _, _) = Create();
        server.AllowConfigure = false;

        launcher.RequestHost(7777, "host-a");

        Assert.Equal(SinglePlayerPhase.Failed, launcher.Phase);
        Assert.Equal(SinglePlayerLauncher.ServerBusyMessage, launcher.StatusMessage);
    }

    [Fact]
    public void RequestStart_WhenConfigureIsRefused_Fails()
    {
        var (launcher, server, _, _) = Create();
        server.AllowConfigure = false;

        launcher.RequestStart();

        Assert.Equal(SinglePlayerPhase.Failed, launcher.Phase);
        Assert.Equal(SinglePlayerLauncher.ServerBusyMessage, launcher.StatusMessage);
    }

    [Fact]
    public async Task Host_ConnectFails_ShutsTheHostedListenerDown_AndSinglePlayerStartsFresh()
    {
        var (launcher, server, connector, coordinator) = Create();
        connector.FailConnect = true;
        launcher.RequestHost(7777, "host-a");
        PresentFrames(launcher, 2);
        launcher.Update();
        await coordinator.WaitForPendingConnectionAsync();
        launcher.Update();

        Assert.Equal(SinglePlayerPhase.Failed, launcher.Phase);
        Assert.Equal(LocalServerListenOptions.Loopback, server.ConfigureCalls[^1]);
        Assert.Equal(LocalServerListenOptions.Loopback, server.ListenOptions);

        connector.FailConnect = false;
        server.ConfigureCalls.Clear();
        launcher.RequestStart();
        Assert.Equal(LocalServerListenOptions.Loopback, Assert.Single(server.ConfigureCalls));
        PresentFrames(launcher, 2);
        launcher.Update();
        Assert.Equal(2, server.StartCalls);
        Assert.Equal(LocalServerListenOptions.Loopback, server.ListenAtStart);
    }

    [Fact]
    public async Task RequestJoin_NeverStartsTheLocalServer_AndIgnoresAContentError()
    {
        var (launcher, server, connector, coordinator) = Create(contentError: "items.json is missing");

        launcher.RequestJoin(new ClientConnectionOptions("game.example.net", "5057", "joiner"));

        Assert.Equal(SinglePlayerPhase.Connecting, launcher.Phase);
        await coordinator.WaitForPendingConnectionAsync();
        launcher.Update();
        Assert.Equal(0, server.StartCalls);
        RemoteClientOptions connect = Assert.Single(connector.Connects);
        Assert.Equal("game.example.net", connect.Host);
        Assert.True(launcher.TryTakeConnectedSession(out RemoteClientSession? session));
        await session!.DisposeAsync();
    }

    [Fact]
    public void RequestJoin_WithAnInvalidPort_Fails_AndDismissReturnsToIdle()
    {
        var (launcher, server, connector, _) = Create();

        launcher.RequestJoin(new ClientConnectionOptions("game.example.net", "notaport", "joiner"));

        Assert.Equal(SinglePlayerPhase.Failed, launcher.Phase);
        Assert.NotEmpty(launcher.StatusMessage);
        Assert.Empty(connector.Connects);
        Assert.Equal(0, server.StartCalls);

        launcher.Dismiss();
        Assert.Equal(SinglePlayerPhase.Idle, launcher.Phase);
        Assert.Equal(string.Empty, launcher.StatusMessage);
    }

    [Fact]
    public void RequestStart_IsIgnoredWhileBusy()
    {
        var (launcher, server, _, _) = Create();
        launcher.RequestStart();
        PresentFrames(launcher, 2);
        launcher.Update();
        launcher.RequestStart();

        Assert.NotEqual(SinglePlayerPhase.Waking, launcher.Phase);
        Assert.Equal(1, server.StartCalls);
    }
}
