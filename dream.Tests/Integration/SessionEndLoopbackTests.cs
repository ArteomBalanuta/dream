using System.Diagnostics;
using System.Net;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Runtime;
using Dream.Tests.Client.Networking;
using Dream.Tests.Utilities;

namespace Dream.Tests.Integration;

// NETR-2 over real loopback TCP: a client whose server goes away reports why within about 8 s,
// whether the server closes its sockets (a stopped integrated server) or hangs with them open.
public class SessionEndLoopbackTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    // Local mode: Game stops the integrated server handle on its way back to the menu; the same
    // happens here from the other side, and the client must notice well within the 8 s budget.
    [Fact]
    public async Task StoppedIntegratedServer_EndsTheClientSession_WithAReason_WithinEightSeconds()
    {
        IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
            0,
            () => DedicatedServerHost.CreateRuntimeAttached(persistence: null),
            IntegratedLocalServerHandle.DefaultConnectionOptions);
        bool serverDisposed = false;
        try
        {
            Assert.True(server.IsReady, server.ErrorMessage);
            await using RemoteClientSession session = await RemoteClientSession.ConnectAsync(
                new RemoteClientOptions("127.0.0.1", server.BoundPort, "end-stop", "end-stop", "end-test"));
            await WaitUntilAsync(() => session.IsJoined, "the join");
            long framesAtJoin = session.InboundFrameCount;
            await WaitUntilAsync(() => session.InboundFrameCount > framesAtJoin + 3, "snapshots");
            Assert.True(session.IsConnected);

            await server.DisposeAsync();
            serverDisposed = true;
            Stopwatch sinceStop = Stopwatch.StartNew();
            await WaitUntilAsync(() => !session.IsConnected, "the session end");

            Assert.True(
                sinceStop.Elapsed <= RemoteClientSession.ServerSilenceTimeout + TimeSpan.FromSeconds(1),
                $"took {sinceStop.Elapsed.TotalSeconds:0.0} s");
            Assert.NotNull(session.DisconnectReason);
            Assert.StartsWith("Connection lost", session.DisconnectReason);
        }
        finally
        {
            if (!serverDisposed)
            {
                await server.DisposeAsync();
            }
        }
    }

    // A hung server: it stops ticking (no snapshots) but its sockets stay open, so only the
    // watchdog can tell. The watchdog runs on a manual clock, so the 8 s cost no real time.
    [Fact]
    public async Task HungServer_ThatStopsSendingSnapshots_IsDetectedByTheWatchdog()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        await using DedicatedServerService service = new(
            host,
            new TcpServerTransport(IPAddress.Loopback, 0),
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            IntegratedLocalServerHandle.DefaultConnectionOptions);
        service.Start();

        ManualTimerTimeProvider clock = new();
        using CancellationTokenSource stopTicks = new();
        Task tickPump = PumpTicksAsync(service, stopTicks.Token);
        Task<ServerConnection> accept = service.AcceptOnThreadPoolAsync();
        await using RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", service.Port, "end-hang", "end-hang", "end-test"),
            timeProvider: clock);
        await accept.WaitAsync(Deadline);
        try
        {
            await WaitUntilAsync(() => session.IsJoined, "the join");
            long framesAtJoin = session.InboundFrameCount;
            await WaitUntilAsync(() => session.InboundFrameCount > framesAtJoin + 3, "snapshots");
        }
        finally
        {
            stopTicks.Cancel();
            await tickPump;
        }

        // The server is now silent with the connection open. Let frames in flight land.
        await WaitForQuietAsync(session);
        Assert.True(session.IsConnected, session.DisconnectReason);
        await WaitUntilAsync(() => clock.TimerCount >= 1, "the watchdog to wait");

        clock.Advance(RemoteClientSession.ServerSilenceTimeout);
        await WaitUntilAsync(() => !session.IsConnected, "the watchdog to end the session");
        Assert.Equal(RemoteClientSession.ServerSilenceDisconnectReason, session.DisconnectReason);
    }

    private static async Task PumpTicksAsync(DedicatedServerService service, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            try
            {
                await Task.Delay(5, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task WaitForQuietAsync(RemoteClientSession session)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        long seen = session.InboundFrameCount;
        Stopwatch quiet = Stopwatch.StartNew();
        while (quiet.Elapsed < TimeSpan.FromMilliseconds(300))
        {
            Assert.True(DateTime.UtcNow < deadline, "The server never went quiet.");
            await Task.Delay(10);
            long now = session.InboundFrameCount;
            if (now != seen)
            {
                seen = now;
                quiet.Restart();
            }
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
}
