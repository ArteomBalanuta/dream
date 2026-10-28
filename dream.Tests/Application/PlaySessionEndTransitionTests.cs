using Xunit;
using Dream.Application;
using Dream.Client.Networking;
using Dream.Client.Scenes;
using Dream.Client.Startup;
using Dream.Server;

namespace Dream.Tests.Application;

// NETR-2: the decision and teardown Game runs when the play session ends. PlayScene and Game need
// a GL context, so they are stood in for by fakes here; SceneTeardownTests checks Game's wiring.
public class PlaySessionEndTransitionTests
{
    [Fact]
    public void PlayScene_IsASessionEndSource()
    {
        Assert.True(typeof(ISessionEndSource).IsAssignableFrom(typeof(PlayScene)));
    }

    [Fact]
    public void HasEnded_IsFalse_WhileTheSessionIsLive()
    {
        FakePlayScene scene = new(reason: null);

        Assert.False(PlaySessionEndTransition.HasEnded(scene, out string? reason));
        Assert.Null(reason);
    }

    [Fact]
    public void HasEnded_ReturnsTheReason_OnceTheSessionEnded()
    {
        FakePlayScene scene = new(reason: null);
        Assert.False(PlaySessionEndTransition.HasEnded(scene, out _));

        scene.Reason = "Connection lost: no data from the server for 8 s";

        Assert.True(PlaySessionEndTransition.HasEnded(scene, out string? reason));
        Assert.Equal("Connection lost: no data from the server for 8 s", reason);
    }

    [Fact]
    public void TearDown_DisposesTheSceneThenTheLocalServer_EachOnce()
    {
        List<string> order = [];
        FakePlayScene scene = new("ended", order);
        FakeLocalServer server = new(order);

        IReadOnlyList<Exception> failures = PlaySessionEndTransition.TearDown(scene, server);

        Assert.Empty(failures);
        Assert.Equal(["scene", "server"], order);
        Assert.Equal(1, scene.DisposeCount);
        Assert.Equal(1, server.DisposeCount);
    }

    [Fact]
    public void TearDown_StillStopsTheLocalServer_WhenTheSceneThrows()
    {
        List<string> order = [];
        FakePlayScene scene = new("ended", order) { ThrowOnDispose = new InvalidOperationException("gl") };
        FakeLocalServer server = new(order);

        IReadOnlyList<Exception> failures = PlaySessionEndTransition.TearDown(scene, server);

        Assert.Same(scene.ThrowOnDispose, Assert.Single(failures));
        Assert.Equal(["scene", "server"], order);
        Assert.Equal(1, server.DisposeCount);
    }

    [Fact]
    public void TearDown_ReturnsBothFailures_WhenBothStepsThrow()
    {
        FakePlayScene scene = new("ended") { ThrowOnDispose = new InvalidOperationException("scene") };
        FakeLocalServer server = new() { ThrowOnDispose = new InvalidOperationException("server") };

        IReadOnlyList<Exception> failures = PlaySessionEndTransition.TearDown(scene, server);

        Assert.Equal(2, failures.Count);
        Assert.Same(scene.ThrowOnDispose, failures[0]);
        Assert.Same(server.ThrowOnDispose, failures[1]);
    }

    // Local mode: Game's handle is an OnDemandLocalServerHandle; the integrated server it started
    // is stopped exactly once by the teardown.
    [Fact]
    public void TearDown_StopsTheStartedIntegratedServer_Once()
    {
        FakeLocalServer integrated = new();
        OnDemandLocalServerHandle handle = new(() => integrated);
        handle.EnsureStarted();

        PlaySessionEndTransition.TearDown(new FakePlayScene("ended"), handle);

        Assert.Equal(1, integrated.DisposeCount);
    }

    // Remote mode: the local server never started, so there is nothing to stop.
    [Fact]
    public void TearDown_StartsNothing_WhenTheLocalServerNeverStarted()
    {
        int starts = 0;
        OnDemandLocalServerHandle handle = new(() =>
        {
            starts++;
            return new FakeLocalServer();
        });

        IReadOnlyList<Exception> failures = PlaySessionEndTransition.TearDown(new FakePlayScene("ended"), handle);

        Assert.Empty(failures);
        Assert.Equal(0, starts);
        Assert.False(handle.IsStarted);
    }

    // A handle whose disposal completes within the timeout is waited for, not abandoned.
    [Fact]
    public void StopLocalServer_WaitsForADisposalThatFinishesInTime()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLocalServer server = new() { Disposal = release.Task };
        List<string> log = [];
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            release.SetResult();
        });

        Exception? failure = PlaySessionEndTransition.StopLocalServer(server, TimeSpan.FromSeconds(10), log.Add);

        Assert.Null(failure);
        Assert.True(release.Task.IsCompleted);
        Assert.Empty(log);
    }

    [Fact]
    public void StopLocalServer_ReturnsTheDisposalFailure_Unwrapped()
    {
        FakeLocalServer server = new() { ThrowOnDispose = new InvalidOperationException("server") };

        Assert.Same(server.ThrowOnDispose, PlaySessionEndTransition.StopLocalServer(server, TimeSpan.FromSeconds(10), _ => { }));
    }

    // W9-4: the connection screen after a session ends shows the ended endpoint as it was.
    [Fact]
    public void ReturnOptions_KeepTheEndedEndpointExactly()
    {
        RemoteClientOptions ended = new("127.0.0.1", 5057, "session-1", "player-1", "client-1");

        ClientConnectionOptions options = PlaySessionEndTransition.ReturnOptions(ended);

        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal("5057", options.PortText);
        Assert.Equal("player-1", options.PlayerName);
    }

    // Constructing the relaunch connector (as Game does when it shows the connection screen)
    // starts nothing; a connect elsewhere passes through untouched and still starts nothing.
    [Fact]
    public async Task RelaunchConnector_PassesOtherEndpointsThrough_AndStartsNothing()
    {
        int starts = 0;
        OnDemandLocalServerHandle handle = new(() =>
        {
            starts++;
            return new FakeLocalServer();
        });
        RecordingConnector inner = new();
        LocalServerRelaunchConnector connector = new(inner, handle, "127.0.0.1", 50123);
        NetworkSessionCoordinator coordinator = new(handle, connector);
        Assert.Equal(0, starts);

        // A loopback dedicated server on another port, and a remote host on the same port.
        coordinator.BeginConnect(new ClientConnectionOptions("127.0.0.1", "5057", "player"));
        await coordinator.WaitForPendingConnectionAsync();
        coordinator.BeginConnect(new ClientConnectionOptions("10.0.0.2", "50123", "player"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(0, starts);
        Assert.False(handle.IsStarted);
        Assert.Equal([("127.0.0.1", 5057), ("10.0.0.2", 50123)], inner.Targets);
    }

    // A connect to the ended integrated server's endpoint starts the new one then, and goes to
    // its port.
    [Fact]
    public async Task RelaunchConnector_StartsTheLocalServerOnConnect_AndUsesItsPort()
    {
        int starts = 0;
        OnDemandLocalServerHandle handle = new(() =>
        {
            starts++;
            return new FakeLocalServer();
        });
        RecordingConnector inner = new();
        NetworkSessionCoordinator coordinator = new(handle, new LocalServerRelaunchConnector(inner, handle, "127.0.0.1", 50123));
        Assert.False(handle.IsStarted);

        coordinator.BeginConnect(new ClientConnectionOptions(" 127.0.0.1 ", "50123", "player"));
        await coordinator.WaitForPendingConnectionAsync();
        coordinator.BeginConnect(new ClientConnectionOptions("127.0.0.1", "50123", "player"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(1, starts);
        Assert.True(handle.IsStarted);
        Assert.Equal([("127.0.0.1", 6123), ("127.0.0.1", 6123)], inner.Targets);
    }

    [Fact]
    public async Task RelaunchConnector_ReportsALocalServerThatFailsToStart()
    {
        OnDemandLocalServerHandle handle = new(() => new FakeLocalServer { Ready = false, Error = "port in use" });
        RecordingConnector inner = new();
        NetworkSessionCoordinator coordinator = new(handle, new LocalServerRelaunchConnector(inner, handle, "127.0.0.1", 50123));

        coordinator.BeginConnect(new ClientConnectionOptions("127.0.0.1", "50123", "player"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Empty(inner.Targets);
        Assert.Equal(ConnectionFlowState.Failed, coordinator.State);
        Assert.Contains("port in use", coordinator.StatusMessage);
    }

    // Records each target and fails the connect: no socket is opened.
    private sealed class RecordingConnector : IRemoteSessionConnector
    {
        public List<(string Host, int Port)> Targets { get; } = [];

        public ValueTask<RemoteClientSession> ConnectAsync(RemoteClientOptions options, CancellationToken cancellationToken = default)
        {
            Targets.Add((options.Host, options.Port));
            return ValueTask.FromException<RemoteClientSession>(new InvalidOperationException("no server in this test"));
        }
    }

    private sealed class FakePlayScene(string? reason, List<string>? order = null) : ISessionEndSource, IDisposable
    {
        public string? Reason { get; set; } = reason;

        public string? SessionEndReason => Reason;

        public Exception? ThrowOnDispose { get; init; }

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            order?.Add("scene");
            if (ThrowOnDispose != null)
            {
                throw ThrowOnDispose;
            }
        }
    }

    private sealed class FakeLocalServer(List<string>? order = null) : ILocalServerHandle
    {
        public bool Ready { get; init; } = true;

        public string? Error { get; init; }

        public bool IsReady => Ready;

        public int BoundPort => Ready ? 6123 : 0;

        public string? ErrorMessage => Error;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public Exception? ThrowOnDispose { get; init; }

        public Task? Disposal { get; init; }

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            order?.Add("server");
            return ThrowOnDispose != null
                ? ValueTask.FromException(ThrowOnDispose)
                : Disposal != null ? new ValueTask(Disposal) : ValueTask.CompletedTask;
        }
    }
}
