using System.Diagnostics;
using System.Net;
using Xunit;
using Xunit.Abstractions;
using Dream.Application;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Physics;
using Dream.Tests.Client.Networking;
using Dream.Tests.Utilities;

namespace Dream.Tests.Integration;

// Wave 9 tester, NETR-2 (server-silence watchdog, return to the connection screen):
// - a session that ends while its join is still pending;
// - the local-mode cycle Game runs: start the integrated server on demand, play, end the session
//   (TearDown order: scene, then server), take a fresh handle, start a new local session;
// - repeated connect / end cycles leak no physics world, thread or handle
//   (Wave9SessionLeakLoopbackTests below).
// ARCH-1c: only the leak test reads process-wide counters, so only it runs alone (TimingSensitive);
// it is its own class because a named collection holds whole classes. The rest of these tests run
// in the parallel pool, one collection each (TestScheduling.SplitClasses).
public class Wave9SessionLifecycleLoopbackTests
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper _output;

    public Wave9SessionLifecycleLoopbackTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // A server that takes the connection and never answers (no hello, no join reply): ConnectAsync
    // returns after the hello timeout with the join pending. The watchdog stays idle until a
    // JoinAccepted (8 s of manual clock pass without ending the session); a server that then goes
    // away ends the pending session as a lost connection, and its disposal stops every loop.
    [Fact]
    public async Task SessionEndingDuringAPendingJoin_ReportsTheLostConnection_AndDisposesPromptly()
    {
        using System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<System.Net.Sockets.Socket> accept = listener.AcceptSocketAsync();
        ManualTimerTimeProvider clock = new();
        Task<RemoteClientSession> connect = RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", port, "w9-pending", "w9-pending", "w9-test"),
            timeProvider: clock).AsTask();
        using System.Net.Sockets.Socket serverSide = await accept.WaitAsync(Deadline);
        DateTime deadline = DateTime.UtcNow + Deadline;
        // The hello timeout runs on the session's (manual) clock. Advance just past it, never
        // toward JoinTimeout: the clock only moves while ConnectAsync has not returned, and in
        // total at most the hello timeout plus one step, however slowly the continuation runs.
        TimeSpan step = TimeSpan.FromMilliseconds(500);
        TimeSpan advanced = TimeSpan.Zero;
        while (!connect.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "ConnectAsync never returned");
            if (advanced <= RemoteClientSession.DefaultServerHelloTimeout)
            {
                clock.Advance(step);
                advanced += step;
            }

            await Task.Delay(20);
        }

        Assert.True(advanced < RemoteClientSession.JoinTimeout, $"advanced {advanced}");

        RemoteClientSession session = await connect;
        try
        {
            Assert.False(session.IsJoined);
            Assert.True(session.IsConnected, session.DisconnectReason);

            clock.Advance(RemoteClientSession.ServerSilenceTimeout + TimeSpan.FromSeconds(1));
            await Task.Delay(200);
            Assert.True(session.IsConnected, $"the watchdog fired before the join: {session.DisconnectReason}");

            serverSide.Close();
            await WaitUntilAsync(() => !session.IsConnected, "the pending session to end");
            _output.WriteLine($"pending join ended: {session.DisconnectReason}");
            Assert.False(session.IsJoined);
            Assert.NotNull(session.DisconnectReason);
        }
        finally
        {
            Stopwatch dispose = Stopwatch.StartNew();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            _output.WriteLine($"session disposed in {dispose.ElapsedMilliseconds} ms");
            Assert.True(dispose.Elapsed < TimeSpan.FromSeconds(6), $"dispose took {dispose.Elapsed}");
        }
    }

    // W9-3: TearDown runs on the render thread. An integrated server whose tick is stuck (the case
    // the watchdog reports in local mode: the server stopped sending, its socket open) used to
    // freeze the window there for good. Now the handle stops on a background task and TearDown
    // waits for it at most its timeout, logs, and abandons it; the scene is still disposed first,
    // on the calling thread, and the abandoned disposal can still finish later.
    [Fact]
    public async Task TearDown_AbandonsAHungLocalServer_AfterItsTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), PlaySessionEndTransition.LocalServerStopTimeout);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HangingLocalServer server = new(release.Task);
        NoopScene scene = new();
        List<string> log = [];
        int callingThread = -1;
        TimeSpan timeout = TimeSpan.FromMilliseconds(500);
        Stopwatch elapsed = Stopwatch.StartNew();
        Task<IReadOnlyList<Exception>> tearDown = Task.Run(() =>
        {
            callingThread = Environment.CurrentManagedThreadId;
            return PlaySessionEndTransition.TearDown(scene, server, timeout, line => { lock (log) { log.Add(line); } });
        });
        try
        {
            IReadOnlyList<Exception> failures = await tearDown.WaitAsync(Deadline);
            elapsed.Stop();
            _output.WriteLine($"TearDown returned after {elapsed.ElapsedMilliseconds} ms");
            Assert.Empty(failures);
            Assert.True(elapsed.Elapsed >= timeout - TimeSpan.FromMilliseconds(50), $"returned before the timeout: {elapsed.Elapsed}");
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"waited too long: {elapsed.Elapsed}");
            Assert.Equal(callingThread, scene.DisposeThread);
            Assert.Equal(1, server.DisposeCount);
            lock (log)
            {
                Assert.Contains(log, line => line.Contains("did not stop within", StringComparison.Ordinal) && line.Contains("abandoning", StringComparison.Ordinal));
            }
        }
        finally
        {
            release.TrySetResult();
        }
    }

    // An abandoned server's late failure is observed and logged, not left unobserved.
    [Fact]
    public async Task TearDown_LogsTheLateFailure_OfAnAbandonedLocalServer()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HangingLocalServer server = new(release.Task);
        List<string> log = [];
        IReadOnlyList<Exception> failures = PlaySessionEndTransition.TearDown(
            new NoopScene(), server, TimeSpan.FromMilliseconds(100), line => { lock (log) { log.Add(line); } });
        Assert.Empty(failures);

        release.TrySetException(new InvalidOperationException("stuck tick"));
        await WaitUntilAsync(() => { lock (log) { return log.Any(line => line.Contains("stuck tick", StringComparison.Ordinal)); } }, "the late failure to be logged");
    }

    // A new integrated server never collides with one that was abandoned still running: each
    // takes a fresh ephemeral port, and both serve at once.
    [Fact]
    public async Task NewIntegratedServer_TakesAFreshPort_WhileAnAbandonedOneStillRuns()
    {
        IntegratedLocalServerHandle abandoned = await IntegratedLocalServerHandle.StartAsync(
            0, () => DedicatedServerHost.CreateRuntimeAttached(persistence: null));
        try
        {
            Assert.True(abandoned.IsReady, abandoned.ErrorMessage);
            OnDemandLocalServerHandle fresh = new(() => IntegratedLocalServerHandle
                .StartAsync(0, () => DedicatedServerHost.CreateRuntimeAttached(persistence: null))
                .AsTask().GetAwaiter().GetResult());
            await using (fresh)
            {
                fresh.EnsureStarted();
                Assert.True(fresh.IsReady, fresh.ErrorMessage);
                Assert.NotEqual(abandoned.BoundPort, fresh.BoundPort);

                NetworkSessionCoordinator coordinator = new(fresh, new RemoteSessionConnector());
                coordinator.BeginConnect(new ClientConnectionOptions("127.0.0.1", fresh.BoundPort.ToString(), "w9-fresh"));
                await coordinator.WaitForPendingConnectionAsync().WaitAsync(Deadline);
                Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session), coordinator.StatusMessage);
                await using (session!)
                {
                    await WaitUntilAsync(() => session!.IsJoined, "the join to the fresh server");
                }
            }
        }
        finally
        {
            await abandoned.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    // W9-4: after a session to Game's own integrated server ends, the connection screen keeps the
    // ended endpoint and starts nothing; a connect to that endpoint starts the new integrated
    // server through the fresh handle and joins it on its own, fresh port.
    [Fact]
    public async Task ReturnToMenu_AfterTheIntegratedServerSession_StartsTheNewServerOnlyOnConnect()
    {
        OnDemandLocalServerHandle first = new(() => IntegratedLocalServerHandle
            .StartAsync(0, () => DedicatedServerHost.CreateRuntimeAttached(persistence: null))
            .AsTask().GetAwaiter().GetResult());
        NetworkSessionCoordinator firstCoordinator = new(first, new RemoteSessionConnector());
        ClientConnectionOptions firstOptions = firstCoordinator.UseLocalServerDefaults("w9-relaunch");
        firstCoordinator.BeginConnect(firstOptions);
        await firstCoordinator.WaitForPendingConnectionAsync().WaitAsync(Deadline);
        Assert.True(firstCoordinator.TryTakeConnectedSession(out RemoteClientSession? firstSession), firstCoordinator.StatusMessage);
        await WaitUntilAsync(() => firstSession!.IsJoined, "the first join");
        RemoteClientOptions endedOptions = firstSession!.Options;
        int firstPort = first.BoundPort;
        Assert.True(first.IsStarted);
        Assert.Empty(PlaySessionEndTransition.TearDown(new SessionScene(firstSession), first));

        OnDemandLocalServerHandle second = new(() => IntegratedLocalServerHandle
            .StartAsync(0, () => DedicatedServerHost.CreateRuntimeAttached(persistence: null))
            .AsTask().GetAwaiter().GetResult());
        await using (second)
        {
            ClientConnectionOptions shown = PlaySessionEndTransition.ReturnOptions(endedOptions);
            NetworkSessionCoordinator coordinator = new(
                second,
                new LocalServerRelaunchConnector(new RemoteSessionConnector(), second, endedOptions.Host, endedOptions.Port));
            Assert.Equal(firstOptions.Host, shown.Host);
            Assert.Equal(firstPort.ToString(), shown.PortText);
            Assert.False(second.IsStarted, "the menu started the integrated server before a connect");

            coordinator.BeginConnect(shown);
            Assert.True(second.IsStarted);
            await coordinator.WaitForPendingConnectionAsync().WaitAsync(Deadline);
            Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session), coordinator.StatusMessage);
            await using (session!)
            {
                await WaitUntilAsync(() => session!.IsJoined, "the join to the relaunched server");
                Assert.Equal(second.BoundPort, session!.Options.Port);
                Assert.NotEqual(firstPort, second.BoundPort);
            }
        }
    }

    internal static void ForceCollection()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(5);
        }
    }

    // Stands in for PlayScene: its Dispose disposes the network session it owns.
    internal sealed class SessionScene(RemoteClientSession session) : IDisposable
    {
        public void Dispose()
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class NoopScene : IDisposable
    {
        public int DisposeThread { get; private set; } = -1;

        public void Dispose()
        {
            DisposeThread = Environment.CurrentManagedThreadId;
        }
    }

    private sealed class HangingLocalServer(Task disposal) : ILocalServerHandle
    {
        private int _disposeCount;

        public bool IsReady => true;

        public int BoundPort => 1;

        public string? ErrorMessage => null;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return new(disposal);
        }
    }
}

// The leak test of Wave9SessionLifecycleLoopbackTests: it reads PhysicsIntegrator.LiveCount and the
// process's thread and handle counts, which any other test running at the same time would move,
// so it runs alone (TimingSensitive).
[Collection(TimingSensitiveCollection.Name)]
public class Wave9SessionLeakLoopbackTests
{
    private static readonly TimeSpan Deadline = Wave9SessionLifecycleLoopbackTests.Deadline;

    private readonly ITestOutputHelper _output;

    public Wave9SessionLeakLoopbackTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Game's local cycle, ten times: each local session ends (half with the server stopping first,
    // as when the integrated server goes away, half in TearDown's order: the session first, then
    // the server), and the next one starts on a fresh handle. Every integrated server takes a fresh
    // ephemeral port (StartAsync() with port 0), the physics world count returns to its baseline
    // after every cycle, and threads and OS handles do not grow from cycle to cycle.
    [Fact]
    public async Task RepeatedLocalSessions_ReuseNothingStale_AndLeakNothing()
    {
        const int Cycles = 10;
        long liveBaseline = PhysicsIntegrator.LiveCount;
        List<int> ports = [];
        List<(long Live, int Threads, int Handles)> samples = [];
        for (int cycle = 0; cycle < Cycles; cycle++)
        {
            OnDemandLocalServerHandle handle = new(() => IntegratedLocalServerHandle
                .StartAsync(0, () => DedicatedServerHost.CreateRuntimeAttached(persistence: null))
                .AsTask().GetAwaiter().GetResult());
            NetworkSessionCoordinator coordinator = new(handle, new RemoteSessionConnector());
            ClientConnectionOptions options = coordinator.ResolveInitialOptions(new ClientConnectionOptions("127.0.0.1", "7777", $"w9-cycle-{cycle}"));
            Assert.True(handle.IsReady, handle.ErrorMessage);
            Assert.Equal(handle.BoundPort.ToString(), options.PortText);
            ports.Add(handle.BoundPort);

            coordinator.BeginConnect(options);
            await coordinator.WaitForPendingConnectionAsync().WaitAsync(Deadline);
            Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session), coordinator.StatusMessage);
            await WaitUntilAsync(() => session!.IsJoined, "the join");
            Assert.True(PhysicsIntegrator.LiveCount > liveBaseline, "the running server's world is not counted");
            long frames = session!.InboundFrameCount;
            await WaitUntilAsync(() => session.InboundFrameCount > frames + 3, "snapshots");

            if (cycle % 2 == 0)
            {
                // The server goes away first; the session notices and ends with a reason.
                await handle.DisposeAsync();
                await WaitUntilAsync(() => !session.IsConnected, "the session end");
                Assert.NotNull(session.DisconnectReason);
                await session.DisposeAsync();
            }
            else
            {
                // TearDown's order: the scene (which owns the session), then the server.
                IReadOnlyList<Exception> failures = PlaySessionEndTransition.TearDown(new Wave9SessionLifecycleLoopbackTests.SessionScene(session), handle);
                Assert.Empty(failures);
            }

            ForceCollection();
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            samples.Add((PhysicsIntegrator.LiveCount, process.Threads.Count, process.HandleCount));
            _output.WriteLine($"cycle {cycle}: port {ports[^1]}, live integrators {samples[^1].Live} (baseline {liveBaseline}), threads {samples[^1].Threads}, handles {samples[^1].Handles}");
            Assert.Equal(liveBaseline, PhysicsIntegrator.LiveCount);
        }

        Assert.Equal(Cycles, ports.Distinct().Count());
        // Warm-up (pools, the JIT, the first socket) is over after a few cycles; from there on
        // nothing may grow with the number of cycles.
        int threadGrowth = samples[^1].Threads - samples[3].Threads;
        int handleGrowth = samples[^1].Handles - samples[3].Handles;
        _output.WriteLine($"cycles 3..{Cycles - 1}: thread growth {threadGrowth}, handle growth {handleGrowth}");
        Assert.True(threadGrowth <= 8, $"threads grew by {threadGrowth}");
        Assert.True(handleGrowth <= 60, $"handles grew by {handleGrowth}");
    }

    private static void ForceCollection() => Wave9SessionLifecycleLoopbackTests.ForceCollection();

    private static Task WaitUntilAsync(Func<bool> condition, string what) => Wave9SessionLifecycleLoopbackTests.WaitUntilAsync(condition, what);
}
