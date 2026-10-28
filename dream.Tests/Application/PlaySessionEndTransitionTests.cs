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

    // M1: the integrated server starts at launch for a loopback config, but the player then played
    // on a remote server that dropped. That session was not the integrated server's: nothing is
    // relaunched, and a connect to the ended endpoint goes to that endpoint.
    [Fact]
    public async Task RemoteSessionEnding_RelaunchesNothing_AndKeepsTheEndpoint()
    {
        await AssertNoRelaunchFor(new RemoteClientOptions("play.example.com", 7777, "session-1", "player-1", "client-1"));
    }

    // M1: a loopback dedicated server on another port than the integrated server's is not it either.
    [Fact]
    public async Task LoopbackDedicatedSessionOnAnotherPortEnding_RelaunchesNothing_AndKeepsTheEndpoint()
    {
        await AssertNoRelaunchFor(new RemoteClientOptions("127.0.0.1", 7777, "session-1", "player-1", "client-1"));
    }

    // Mirrors Game.ReturnToStartupIfSessionEnded: the ended handle ran the integrated server on
    // 6123, the session was elsewhere, and the connection screen comes back with a fresh handle.
    private static async Task AssertNoRelaunchFor(RemoteClientOptions ended)
    {
        FakeLocalServer integrated = new();
        OnDemandLocalServerHandle endedHandle = new(() => integrated);
        endedHandle.EnsureStarted();
        Assert.True(endedHandle.IsReady);

        RemoteClientOptions? relaunchFor = PlaySessionEndTransition.RelaunchTarget(endedHandle, ended);
        Assert.Null(relaunchFor);
        Assert.Empty(PlaySessionEndTransition.TearDown(new FakePlayScene("ended"), endedHandle));

        int starts = 0;
        OnDemandLocalServerHandle fresh = new(() =>
        {
            starts++;
            return new FakeLocalServer();
        });
        RecordingConnector inner = new();
        IRemoteSessionConnector connector = relaunchFor != null
            ? new LocalServerRelaunchConnector(inner, fresh, relaunchFor.Host, relaunchFor.Port)
            : inner;
        NetworkSessionCoordinator coordinator = new(fresh, connector);
        ClientConnectionOptions returned = PlaySessionEndTransition.ReturnOptions(ended);
        Assert.Equal(ended.Host, returned.Host);
        Assert.Equal(ended.Port.ToString(), returned.PortText);

        coordinator.BeginConnect(returned);
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(0, starts);
        Assert.False(fresh.IsStarted);
        Assert.Equal([(ended.Host, ended.Port)], inner.Targets);
    }

    // The integrated server's own session is recognized however the host was spelled.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData(" LocalHost ")]
    [InlineData("::1")]
    public void RelaunchTarget_IsTheEndedEndpoint_ForASessionOnTheIntegratedServer(string host)
    {
        OnDemandLocalServerHandle handle = new(() => new FakeLocalServer());
        handle.EnsureStarted();
        RemoteClientOptions ended = new(host, 6123, "session-1", "player-1", "client-1");

        Assert.Same(ended, PlaySessionEndTransition.RelaunchTarget(handle, ended));
    }

    // A handle that never started, or one whose start failed, served no session.
    [Fact]
    public void RelaunchTarget_IsNull_WhenTheIntegratedServerNeverRan()
    {
        RemoteClientOptions ended = new("127.0.0.1", 6123, "session-1", "player-1", "client-1");
        OnDemandLocalServerHandle neverStarted = new(() => new FakeLocalServer());
        OnDemandLocalServerHandle failed = new(() => new FakeLocalServer { Ready = false, Error = "port in use" });
        failed.EnsureStarted();

        Assert.Null(PlaySessionEndTransition.RelaunchTarget(neverStarted, ended));
        Assert.Null(PlaySessionEndTransition.RelaunchTarget(failed, ended));
    }

    // S5: the ended endpoint matches on any loopback spelling, and the relaunched server is always
    // dialled on 127.0.0.1 at its own port.
    [Theory]
    [InlineData("127.0.0.1", "localhost")]
    [InlineData("127.0.0.1", "::1")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("::1", " LOCALHOST ")]
    public async Task RelaunchConnector_MatchesTheEndedEndpointOnAnyLoopbackSpelling(string endedHost, string connectHost)
    {
        int starts = 0;
        OnDemandLocalServerHandle handle = new(() =>
        {
            starts++;
            return new FakeLocalServer();
        });
        RecordingConnector inner = new();
        NetworkSessionCoordinator coordinator = new(handle, new LocalServerRelaunchConnector(inner, handle, endedHost, 50123));

        coordinator.BeginConnect(new ClientConnectionOptions(connectHost, "50123", "player"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(1, starts);
        Assert.Equal([("127.0.0.1", 6123)], inner.Targets);
    }

    // A non-loopback ended host still matches only itself.
    [Fact]
    public async Task RelaunchConnector_DoesNotTreatLoopbackAsARemoteEndedHost()
    {
        int starts = 0;
        OnDemandLocalServerHandle handle = new(() =>
        {
            starts++;
            return new FakeLocalServer();
        });
        RecordingConnector inner = new();
        NetworkSessionCoordinator coordinator = new(handle, new LocalServerRelaunchConnector(inner, handle, "10.0.0.2", 50123));

        coordinator.BeginConnect(new ClientConnectionOptions("localhost", "50123", "player"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(0, starts);
        Assert.Equal([("localhost", 50123)], inner.Targets);
    }

    // M2: the ended integrated server's stop outlasts the teardown's wait and is abandoned, but it
    // is still saving to the world database the next server opens. The relaunch on connect waits
    // for it without blocking the caller, shows that the previous server is still saving, and
    // creates the new server only after the old disposal has finished.
    [Fact]
    public async Task Relaunch_WaitsForTheAbandonedServerToFinishSaving_BeforeStartingTheNextOne()
    {
        LocalServerShutdownGate gate = new();
        TaskCompletionSource oldDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLocalServer oldServer = new() { Disposal = oldDisposal.Task };
        List<string> log = [];

        IReadOnlyList<Exception> failures = PlaySessionEndTransition.TearDown(
            new FakePlayScene("ended"),
            oldServer,
            TimeSpan.FromMilliseconds(50),
            line => { lock (log) { log.Add(line); } },
            gate);
        Assert.Empty(failures);
        Assert.Contains(log, line => line.Contains("abandoning", StringComparison.Ordinal));
        Assert.True(gate.IsPending);

        int starts = 0;
        bool oldDisposalDoneAtStart = false;
        OnDemandLocalServerHandle fresh = new(
            () =>
            {
                oldDisposalDoneAtStart = oldDisposal.Task.IsCompleted;
                Interlocked.Increment(ref starts);
                return new FakeLocalServer();
            },
            startAfter: () => gate.Pending);
        RecordingConnector inner = new();
        NetworkSessionCoordinator coordinator = new(fresh, new LocalServerRelaunchConnector(inner, fresh, "127.0.0.1", 50123));

        // BeginConnect returns at once (the update thread is not blocked) with the start queued.
        coordinator.BeginConnect(new ClientConnectionOptions("127.0.0.1", "50123", "player"));
        Task connect = coordinator.WaitForPendingConnectionAsync();
        await Task.Delay(100);

        Assert.False(connect.IsCompleted);
        Assert.True(coordinator.IsBusy);
        Assert.Equal(0, Volatile.Read(ref starts));
        Assert.True(fresh.IsStarted);
        Assert.False(fresh.IsReady);
        Assert.True(fresh.IsWaitingForPreviousServer);
        Assert.Contains("still saving", coordinator.LocalServerStatus, StringComparison.Ordinal);
        Assert.Empty(inner.Targets);

        oldDisposal.SetResult();
        await connect.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, starts);
        Assert.True(oldDisposalDoneAtStart, "The new server was created before the old one finished saving.");
        Assert.False(gate.IsPending);
        Assert.Equal([("127.0.0.1", 6123)], inner.Targets);
    }

    // Use Local Server goes through the same handle: it never starts a server over one still saving.
    [Fact]
    public async Task UseLocalServer_QueuesTheStartBehindAPreviousServerStillSaving()
    {
        LocalServerShutdownGate gate = new();
        TaskCompletionSource oldDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Track(oldDisposal.Task);
        int starts = 0;
        OnDemandLocalServerHandle fresh = new(
            () =>
            {
                Interlocked.Increment(ref starts);
                return new FakeLocalServer();
            },
            startAfter: () => gate.Pending);
        NetworkSessionCoordinator coordinator = new(fresh, new RecordingConnector());

        coordinator.EnsureLocalServerStarted();

        Assert.Equal(0, Volatile.Read(ref starts));
        Assert.Equal(OnDemandLocalServerHandle.WaitingForPreviousServerStatus, coordinator.LocalServerStatus);

        oldDisposal.SetResult();
        await fresh.WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, starts);
        Assert.True(fresh.IsReady);
        Assert.Equal("Local server ready on 127.0.0.1:6123", coordinator.LocalServerStatus);
    }

    // A handle disposed while its start still waits never starts a server.
    [Fact]
    public async Task OnDemandHandle_DisposedWhileWaiting_NeverStartsTheServer()
    {
        TaskCompletionSource oldDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        OnDemandLocalServerHandle fresh = new(
            () =>
            {
                Interlocked.Increment(ref starts);
                return new FakeLocalServer();
            },
            startAfter: () => oldDisposal.Task);
        fresh.EnsureStarted();

        ValueTask dispose = fresh.DisposeAsync();
        Assert.False(dispose.IsCompleted);
        oldDisposal.SetResult();
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, starts);
    }

    // A failed earlier stop does not fault the gate or keep the next server from starting.
    [Fact]
    public async Task ShutdownGate_CompletesAfterEveryTrackedStop_EvenAFaultedOne()
    {
        LocalServerShutdownGate gate = new();
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Track(first.Task);
        gate.Track(second.Task);

        first.SetException(new InvalidOperationException("disk"));
        await Task.Delay(20);
        Assert.True(gate.IsPending);

        second.SetResult();
        await gate.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(gate.Pending.IsFaulted);
    }

    // S3: the window close waits for the current server and for an earlier one still saving,
    // logging progress, well past the session-end timeout.
    [Fact]
    public void StopLocalServerForExit_WaitsForEveryServerStillSaving()
    {
        LocalServerShutdownGate gate = new();
        TaskCompletionSource earlier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource current = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Track(earlier.Task);
        FakeLocalServer server = new() { Disposal = current.Task };
        List<string> log = [];
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            current.SetResult();
            await Task.Delay(150);
            earlier.SetResult();
        });

        Exception? failure = PlaySessionEndTransition.StopLocalServerForExit(
            server,
            gate,
            TimeSpan.FromSeconds(30),
            line => { lock (log) { log.Add(line); } },
            TimeSpan.FromMilliseconds(50));

        Assert.Null(failure);
        Assert.True(earlier.Task.IsCompleted && current.Task.IsCompleted);
        Assert.Equal(1, server.DisposeCount);
        Assert.Contains(log, line => line.Contains("waiting for the local server to finish saving", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("exiting without it", StringComparison.Ordinal));
    }

    [Fact]
    public void StopLocalServerForExit_GivesUpAfterItsBudget_AndReturnsTheFailureOtherwise()
    {
        LocalServerShutdownGate gate = new();
        FakeLocalServer hung = new() { Disposal = new TaskCompletionSource().Task };
        List<string> log = [];

        Assert.Null(PlaySessionEndTransition.StopLocalServerForExit(
            hung, gate, TimeSpan.FromMilliseconds(100), line => { lock (log) { log.Add(line); } }, TimeSpan.FromMilliseconds(30)));
        Assert.Contains(log, line => line.Contains("exiting without it", StringComparison.Ordinal));

        FakeLocalServer failing = new() { ThrowOnDispose = new InvalidOperationException("server") };
        Assert.Same(
            failing.ThrowOnDispose,
            PlaySessionEndTransition.StopLocalServerForExit(failing, new LocalServerShutdownGate(), TimeSpan.FromSeconds(10), _ => { }));
    }

    // S3: the exit budget comes from the persistence constants, not a number of its own.
    [Fact]
    public void LocalServerExitTimeout_IsTheDrainDeadlinePlusBusyTimeout()
    {
        string pragmas = (string)typeof(Dream.Server.Persistence.SqliteSchemaBootstrapper)
            .GetField("ConnectionPragmas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;
        Assert.Contains($"busy_timeout={(int)PlaySessionEndTransition.PersistenceBusyTimeout.TotalMilliseconds};", pragmas);

        TimeSpan floor = Dream.Server.Persistence.PersistenceWriteQueue.DefaultDisposeDrainDeadline
            + PlaySessionEndTransition.PersistenceBusyTimeout;
        Assert.True(PlaySessionEndTransition.LocalServerExitTimeout >= floor);
        Assert.True(PlaySessionEndTransition.LocalServerExitTimeout <= floor + TimeSpan.FromSeconds(5));
        Assert.True(PlaySessionEndTransition.LocalServerExitTimeout > PlaySessionEndTransition.LocalServerStopTimeout);
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
