using Xunit;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;

namespace Dream.Tests.Client.Startup;

// The desktop used to start the integrated server (a second authoritative world, tick loop and
// open port) on every launch, even when joining a remote server.
public class OnDemandLocalServerTests
{
    [Fact]
    public void RemoteConfiguration_DoesNotStartLocalServer()
    {
        CountingStart start = new();
        OnDemandLocalServerHandle handle = new(start.Start);
        NetworkSessionCoordinator coordinator = new(handle, new NoopConnector());

        ClientConnectionOptions options = coordinator.ResolveInitialOptions(
            new ClientConnectionOptions("game.example.net", "5057", "player-a"));
        coordinator.BeginConnect(options);

        Assert.Equal(0, start.Count);
        Assert.False(handle.IsStarted);
        Assert.Equal("game.example.net", options.Host);
    }

    [Fact]
    public async Task ExplicitLoopbackConnect_ReachesTheRequestedPortWithoutStartingTheLocalServer()
    {
        // A separately running dedicated server on 127.0.0.1:5057 (perf harness, `dream --server`)
        // must not be replaced by the on-demand integrated server. Found by the 2026-10-27 perf run.
        CountingStart start = new();
        RecordingConnector connector = new();
        NetworkSessionCoordinator coordinator = new(new OnDemandLocalServerHandle(start.Start), connector);

        coordinator.BeginConnect(new ClientConnectionOptions("127.0.0.1", "5057", "player-a"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(0, start.Count);
        RemoteClientOptions requested = Assert.Single(connector.Requests);
        Assert.Equal("127.0.0.1", requested.Host);
        Assert.Equal(5057, requested.Port);
    }

    [Fact]
    public void LoopbackConfiguration_StartsLocalServerAndUsesItsPort()
    {
        CountingStart start = new();
        NetworkSessionCoordinator coordinator = new(new OnDemandLocalServerHandle(start.Start), new NoopConnector());

        ClientConnectionOptions options = coordinator.ResolveInitialOptions(
            new ClientConnectionOptions("localhost", "5057", "player-a"));

        Assert.Equal(1, start.Count);
        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal("6200", options.PortText);
        Assert.Equal(ConnectionFlowState.ReadyToConnect, coordinator.State);
    }

    [Fact]
    public void UseLocalServerDefaults_StartsLocalServerOnce()
    {
        CountingStart start = new();
        NetworkSessionCoordinator coordinator = new(new OnDemandLocalServerHandle(start.Start), new NoopConnector());

        coordinator.UseLocalServerDefaults("player-a");
        coordinator.UseLocalServerDefaults("player-a");

        Assert.Equal(1, start.Count);
        Assert.True(coordinator.IsLocalServerReady);
    }

    [Fact]
    public void NotStartedHandle_ReportsIdleStatusInsteadOfWaiting()
    {
        NetworkSessionCoordinator coordinator = new(
            new OnDemandLocalServerHandle(new CountingStart().Start),
            new NoopConnector());

        Assert.Equal(ConnectionFlowState.ReadyToConnect, coordinator.State);
        Assert.Contains("not started", coordinator.LocalServerStatus);
    }

    [Fact]
    public async Task ResetFailure_DuringAPendingStart_DoesNothing_AndTheStartStillRunsOnce()
    {
        CountingStart start = new();
        TaskCompletionSource previousStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OnDemandLocalServerHandle handle = new(start.Start, () => previousStop.Task);

        handle.EnsureStarted();
        Assert.True(handle.IsWaitingForPreviousServer);

        handle.ResetFailure();
        handle.EnsureStarted();

        Assert.True(handle.IsStarted);
        Assert.True(handle.IsWaitingForPreviousServer);
        Assert.Equal(0, start.Count);

        previousStop.SetResult();
        await handle.WaitForStartAsync();

        Assert.Equal(1, start.Count);
        Assert.True(handle.IsReady);
    }

    [Fact]
    public void StartFunc_ReceivesTheConfiguredListenOptions()
    {
        List<LocalServerListenOptions> seen = [];
        OnDemandLocalServerHandle handle = new(listen =>
        {
            seen.Add(listen);
            return new ReadyHandle();
        });
        Assert.Equal(LocalServerListenOptions.Loopback, handle.ListenOptions);

        Assert.True(handle.TryConfigureListen(LocalServerListenOptions.Hosted(5999)));
        handle.EnsureStarted();

        Assert.Equal(LocalServerListenOptions.Hosted(5999), Assert.Single(seen));
        Assert.Equal(LocalServerListenOptions.Hosted(5999), handle.ListenOptions);
    }

    [Fact]
    public void StartFunc_WithoutConfiguration_GetsLoopback()
    {
        LocalServerListenOptions? seen = null;
        OnDemandLocalServerHandle handle = new(listen =>
        {
            seen = listen;
            return new ReadyHandle();
        });

        handle.EnsureStarted();

        Assert.Equal(LocalServerListenOptions.Loopback, seen);
    }

    [Fact]
    public void TryConfigureListen_IsRefusedAfterASuccessfulStart_AndAllowedAfterAFailedOneIsReset()
    {
        bool fail = true;
        OnDemandLocalServerHandle handle = new(_ => fail ? new FailedHandle() : new ReadyHandle());

        handle.EnsureStarted();
        Assert.NotNull(handle.ErrorMessage);
        Assert.True(handle.TryConfigureListen(LocalServerListenOptions.Hosted(5998)));

        handle.ResetFailure();
        fail = false;
        Assert.True(handle.TryConfigureListen(LocalServerListenOptions.Hosted(5997)));
        handle.EnsureStarted();

        Assert.True(handle.IsReady);
        Assert.False(handle.TryConfigureListen(LocalServerListenOptions.Loopback));
        Assert.Equal(LocalServerListenOptions.Hosted(5997), handle.ListenOptions);
    }

    [Fact]
    public void TryConfigureListen_IsRefusedWhileAStartIsPending()
    {
        TaskCompletionSource previousStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OnDemandLocalServerHandle handle = new(_ => new ReadyHandle(), () => previousStop.Task);

        handle.EnsureStarted();

        Assert.False(handle.TryConfigureListen(LocalServerListenOptions.Hosted(5996)));
        Assert.Equal(LocalServerListenOptions.Loopback, handle.ListenOptions);
        previousStop.SetResult();
    }

    private sealed class FailedHandle : ILocalServerHandle
    {
        public bool IsReady => false;

        public int BoundPort => 0;

        public string? ErrorMessage => "boom";

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingStart
    {
        public int Count { get; private set; }

        public ILocalServerHandle Start()
        {
            Count++;
            return new ReadyHandle();
        }
    }

    private sealed class ReadyHandle : ILocalServerHandle
    {
        public bool IsReady => true;

        public int BoundPort => 6200;

        public string? ErrorMessage => null;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingConnector : IRemoteSessionConnector
    {
        public List<RemoteClientOptions> Requests { get; } = [];

        public ValueTask<RemoteClientSession> ConnectAsync(
            RemoteClientOptions options,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(options);
            return ValueTask.FromException<RemoteClientSession>(new InvalidOperationException("not connected in tests"));
        }
    }

    private sealed class NoopConnector : IRemoteSessionConnector
    {
        public ValueTask<RemoteClientSession> ConnectAsync(
            RemoteClientOptions options,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<RemoteClientSession>(new InvalidOperationException("not used"));
        }
    }
}
