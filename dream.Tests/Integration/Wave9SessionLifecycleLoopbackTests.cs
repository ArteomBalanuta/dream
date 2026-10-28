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
// - repeated connect / end cycles leak no physics world, thread or handle.
// The leak test reads process-wide counters, so it runs alone (TimingSensitive).
[Collection(TimingSensitiveCollection.Name)]
public class Wave9SessionLifecycleLoopbackTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

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
        while (!connect.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "ConnectAsync never returned");
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(20);
        }

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
                IReadOnlyList<Exception> failures = PlaySessionEndTransition.TearDown(new SessionScene(session), handle);
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

    // TearDown runs on the render thread and waits for the local server handle without a bound.
    // An integrated server whose tick is stuck (the case the watchdog reports in local mode: the
    // server stopped sending, its socket open) would freeze the window there. Pinned behaviour:
    // TearDown does not return while the handle's disposal does not complete. See W9 report.
    [Fact]
    public async Task TearDown_WaitsForTheLocalServerHandle_WithoutABound()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HangingLocalServer server = new(release.Task);
        Task<IReadOnlyList<Exception>> tearDown = Task.Run(() => PlaySessionEndTransition.TearDown(new NoopScene(), server));
        try
        {
            await Task.Delay(1000);
            Assert.False(tearDown.IsCompleted, "TearDown returned while the server handle was still disposing");
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Empty(await tearDown.WaitAsync(Deadline));
    }

    private static void ForceCollection()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(5);
        }
    }

    // Stands in for PlayScene: its Dispose disposes the network session it owns.
    private sealed class SessionScene(RemoteClientSession session) : IDisposable
    {
        public void Dispose()
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class NoopScene : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class HangingLocalServer(Task disposal) : ILocalServerHandle
    {
        public bool IsReady => true;

        public int BoundPort => 1;

        public string? ErrorMessage => null;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public ValueTask DisposeAsync() => new(disposal);
    }
}
