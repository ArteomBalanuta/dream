using System.Net;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;

namespace Dream.Tests.Server;

// Wave 8 tester, native crash hunt. Smoke8 run 2 of 5 (Release, 20 s) died with 0xC0000005 in
//   btDynamicsWorld_removeRigidBody <- PhysicsIntegrator.ResetMapBodies <- PhysicsIntegrator.Dispose
//   <- WorldSessionRuntime.Dispose <- DedicatedServerHost.Dispose <- LoadRunner.RunAsync
// right after the bots disconnected and the server was shut down.
//
// Cause: DedicatedServerService starts each connection's receive loop fire-and-forget
// (`_ = RunConnectionLoopAsync(...)`, DedicatedServerService.cs:196). When a peer closes (or the
// service is cancelled), the loop's finally calls DedicatedServerHost.ReleaseConnection on a
// thread-pool thread (DedicatedServerService.cs:484), which releases the runtime seat and removes
// the session's player body from the Bullet world (RemoteSessionPlayerState ->
// PhysicsIntegrator.RemoveSessionPlayerBody -> World.RemoveRigidBody). Neither RunAsync nor
// DisposeAsync waits for those loops, and DedicatedServerHost.Dispose (DedicatedServerHost.cs:356)
// does not take _authoritySync, so the caller's host.Dispose() (LoadRunner.cs:93,
// IntegratedLocalServerHandle.cs:111, every loopback test) runs ResetMapBodies / World.Dispose on
// one thread while a release still removes a body on another: concurrent swap-removes in
// btCollisionWorld's object array (AV) or a removal from a freed world (heap corruption: the
// "Internal CLR error 0x80131506" seen earlier).
//
// The service-level test blocks a release inside the host (ReleasingSessionForTest) and checks that
// the shutdown sequence every caller uses (service.DisposeAsync, then host.Dispose) does not finish
// while that release is still in flight. DedicatedServerHost.Dispose takes the tick lock and sets a
// disposed flag under it, so this holds on its own: Dispose waits for the in-flight release, and a
// release, message or tick that comes later leaves the runtime alone. (The service may also await
// its connection loops before DisposeAsync returns; either way the sequence waits.)
public class Wave8ShutdownReleaseRaceTests
{
    private static readonly TimeSpan Completion = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ServiceShutdown_WaitsForInFlightReleases()
    {
        ShutdownProbe probe = await RunShutdownWithBlockedReleaseAsync();
        Assert.True(probe.ReleaseStarted, "the client disconnect never reached ReleaseConnection");
        Assert.False(probe.ShutdownCompletedWhileReleaseBlocked,
            "service.DisposeAsync + host.Dispose finished while a release was still touching the runtime");
    }

    // The host on its own, without a service or transport: Dispose waits for a release that is
    // inside the runtime, and every entry point called after Dispose is a no-op for the runtime.
    [Fact]
    public void HostDispose_WaitsForAnInFlightRelease_AndLaterCallsLeaveTheRuntimeAlone()
    {
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        using ManualResetEventSlim releaseEntered = new();
        using ManualResetEventSlim releaseGate = new();
        List<string> releasedSessions = [];
        try
        {
            Assert.IsType<JoinAcceptedMessage>(Assert.Single(
                host.HandleClientMessage("connection-a", new ClientJoinRequestMessage("session-a", "A"))));
            Assert.IsType<JoinAcceptedMessage>(Assert.Single(
                host.HandleClientMessage("connection-b", new ClientJoinRequestMessage("session-b", "B"))));
            host.ReleasingSessionForTest = session =>
            {
                lock (releasedSessions)
                {
                    releasedSessions.Add(session);
                }

                if (session == "session-a")
                {
                    releaseEntered.Set();
                    releaseGate.Wait(Completion);
                }
            };

            Task release = Task.Run(() => host.ReleaseConnection("connection-a"));
            Assert.True(releaseEntered.Wait(Completion), "the release never reached the runtime");
            Task dispose = Task.Run(host.Dispose);
            Assert.False(dispose.Wait(TimeSpan.FromMilliseconds(500)), "Dispose ran while a release was inside the runtime");
            releaseGate.Set();
            Assert.True(release.Wait(Completion));
            Assert.True(dispose.Wait(Completion));

            // Late arrivals: a connection loop's release, a message, a tick, a resync, a snapshot.
            host.ReleaseConnection("connection-b");
            Assert.Empty(host.HandleClientMessage("connection-c", new ClientJoinRequestMessage("session-c", "C")));
            Assert.Equal(0, host.AdvanceTime(1d));
            Assert.False(host.RequestWorldResyncIfConnected("connection-b"));
            host.DiscardUndeliveredSnapshotDeltas();
            Assert.Throws<ObjectDisposedException>(() => host.CreateSnapshotMessage());
            host.Dispose();

            lock (releasedSessions)
            {
                Assert.Equal(["session-a"], releasedSessions);
            }

            Assert.Equal(0, host.ConnectionCount);
        }
        finally
        {
            releaseGate.Set();
            host.ReleasingSessionForTest = null;
            host.Dispose();
        }
    }

    // Deterministic interleaving of what the load run hit by chance: the server reads the peer's
    // close late (the gated transport holds the EOF), after the tick loop has stopped; the release
    // then runs on the receive thread (blocked in the hook) while the caller finishes the shutdown.
    private static async Task<ShutdownProbe> RunShutdownWithBlockedReleaseAsync()
    {
        using ManualResetEventSlim eofGate = new();
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        DedicatedServerService service = new(
            host,
            new GatedEofServerTransport(new TcpServerTransport(IPAddress.Loopback, 0), eofGate),
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer());
        service.Start();
        using CancellationTokenSource shutdown = new();
        Task run = Task.Run(() => service.RunAsync(shutdown.Token));

        using ManualResetEventSlim releaseEntered = new();
        using ManualResetEventSlim releaseGate = new();
        host.ReleasingSessionForTest = _ =>
        {
            releaseEntered.Set();
            releaseGate.Wait(Completion);
        };

        bool serviceDisposed = false;
        try
        {
            RemoteClientSession client = await RemoteClientSession.ConnectAsync(
                new RemoteClientOptions("127.0.0.1", service.Port, "w8-shutdown", "w8-shutdown", "shutdown-race"));
            await using (client)
            {
                await WaitForJoinAsync(client);
            }

            // 1. Stop the server; its tick loop exits. The connection's receive loop is still
            //    holding the peer's close.
            shutdown.Cancel();
            try
            {
                await run.WaitAsync(Completion);
            }
            catch (OperationCanceledException)
            {
            }

            // 2. The receive loop sees the close and releases the connection (blocked in the hook,
            //    inside ReleaseConnection, with the runtime seat not yet released).
            eofGate.Set();
            if (!releaseEntered.Wait(Completion))
            {
                return new ShutdownProbe(false, false);
            }

            // 3. The rest of every caller's shutdown: service.DisposeAsync(), then host.Dispose().
            Task shutdownRest = Task.Run(async () =>
            {
                await service.DisposeAsync();
                serviceDisposed = true;
                host.Dispose();
            });
            Task finished = await Task.WhenAny(shutdownRest, Task.Delay(TimeSpan.FromSeconds(2)));
            bool completedWhileBlocked = ReferenceEquals(finished, shutdownRest) && !releaseGate.IsSet;
            releaseGate.Set();
            await shutdownRest.WaitAsync(Completion);
            Assert.Equal(0, host.ConnectionCount);
            return new ShutdownProbe(true, completedWhileBlocked);
        }
        finally
        {
            eofGate.Set();
            releaseGate.Set();
            if (!shutdown.IsCancellationRequested)
            {
                shutdown.Cancel();
            }

            try
            {
                await run.WaitAsync(Completion);
            }
            catch (OperationCanceledException)
            {
            }

            if (!serviceDisposed)
            {
                await service.DisposeAsync();
            }

            // Only dispose the host once the release has left it, or this test would be the crash.
            DateTime deadline = DateTime.UtcNow + Completion;
            while (host.ConnectionCount > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            host.ReleasingSessionForTest = null;
            host.Dispose();
        }
    }

    private static async Task WaitForJoinAsync(RemoteClientSession client)
    {
        DateTime deadline = DateTime.UtcNow + Completion;
        while (DateTime.UtcNow < deadline)
        {
            while (client.TryDequeueInbound(out IServerToClientMessage? message))
            {
                if (message is JoinAcceptedMessage)
                {
                    return;
                }
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("join was never accepted");
    }

    private sealed record ShutdownProbe(bool ReleaseStarted, bool ShutdownCompletedWhileReleaseBlocked);

    private sealed class GatedEofServerTransport(IServerTransport inner, ManualResetEventSlim eofGate) : IServerTransport
    {
        public int Port => inner.Port;

        public void Start() => inner.Start();

        public async ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default)
        {
            return new GatedEofConnection(await inner.AcceptClientAsync(cancellationToken), eofGate);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // Returns the peer's close (or a receive error) only once the gate opens, ignoring cancellation.
    private sealed class GatedEofConnection(ITransportConnection inner, ManualResetEventSlim eofGate) : ITransportConnection
    {
        public string ConnectionId => inner.ConnectionId;

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            inner.SendAsync(payload, cancellationToken);

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read;
            try
            {
                read = await inner.ReceiveAsync(buffer, CancellationToken.None);
            }
            catch (Exception)
            {
                read = 0;
            }

            if (read > 0)
            {
                return read;
            }

            await Task.Run(() => eofGate.Wait(Completion));
            return 0;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
