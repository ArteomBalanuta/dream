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
// The test blocks a release inside the host (ReleasingSessionForTest) and checks that the shutdown
// sequence every caller uses does not complete while that release is still in flight.
//
// Fixed in the wave 8 fix loop: the service tracks every connection loop; RunAsync and DisposeAsync
// close the loops' transports and await them (bounded by
// DedicatedServerService.ConnectionLoopShutdownTimeout) before returning. The tester's
// "CurrentlyReturnsWhile..." test, which pinned the bug, was replaced by enabling the target.
public class Wave8ShutdownReleaseRaceTests
{
    private static readonly TimeSpan Completion = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ServiceShutdown_TargetWaitsForInFlightReleases()
    {
        ShutdownProbe probe = await RunShutdownWithBlockedReleaseAsync();
        Assert.True(probe.ReleaseStarted, "the client disconnect never reached ReleaseConnection");
        Assert.False(probe.ShutdownCompletedWhileReleaseBlocked, "DisposeAsync returned while a release was still in flight");
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
            Task disposeService = service.DisposeAsync().AsTask();
            Task finished = await Task.WhenAny(disposeService, Task.Delay(TimeSpan.FromSeconds(2)));
            bool completedWhileBlocked = ReferenceEquals(finished, disposeService) && !releaseGate.IsSet;
            releaseGate.Set();
            await disposeService.WaitAsync(Completion);
            serviceDisposed = true;
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
