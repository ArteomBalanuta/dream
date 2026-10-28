using System.Net;
using System.Net.Sockets;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Runtime;
using Dream.Tests.Utilities;

namespace Dream.Tests.Server;

// Wave 11 tester bug B: a client that disconnects normally was reported as a connection error
// (server.connection.error, IOException "connection reset"). Disposing a TcpClientTransport with
// bytes still unread makes Windows send an RST, so the server's pending read fails with a reset
// instead of returning 0. A reset (or abort) by the peer is a normal disconnect: logged at info
// level, not raised as DedicatedServerService.ConnectionError.
public class ServerConnectionResetClassificationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public void IsPeerReset_IsTrueOnlyForAResetOrAbortedSocket()
    {
        Assert.True(DedicatedServerService.IsPeerReset(new SocketException((int)SocketError.ConnectionReset)));
        Assert.True(DedicatedServerService.IsPeerReset(new SocketException((int)SocketError.ConnectionAborted)));
        Assert.True(DedicatedServerService.IsPeerReset(new IOException("reset", new SocketException((int)SocketError.ConnectionReset))));
        Assert.True(DedicatedServerService.IsPeerReset(new IOException("aborted", new SocketException((int)SocketError.ConnectionAborted))));

        Assert.False(DedicatedServerService.IsPeerReset(new IOException("other", new SocketException((int)SocketError.TimedOut))));
        Assert.False(DedicatedServerService.IsPeerReset(new IOException("no inner")));
        Assert.False(DedicatedServerService.IsPeerReset(new InvalidOperationException("decode", new SocketException((int)SocketError.ConnectionReset))));
        Assert.False(DedicatedServerService.IsPeerReset(new SocketException((int)SocketError.HostUnreachable)));
    }

    // A client that closes with the server's bytes still unread makes Windows send an RST: the
    // server's pending read fails with an IOException (ConnectionReset), and the connection is
    // released without a connection error. (Measured: a linger-0 close with nothing unread reads
    // as a plain EOF on loopback, so the unread bytes are what force the reset here.)
    [Fact]
    public async Task ClientClosingWithUnreadServerBytes_IsANormalDisconnect_NotAConnectionError()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        await using DedicatedServerService service = new(
            host,
            new TcpServerTransport(IPAddress.Loopback, 0),
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer());
        service.Start();
        List<ServerConnectionError> errors = [];
        service.ConnectionError += error =>
        {
            lock (errors)
            {
                errors.Add(error);
            }
        };

        Task<ServerConnection> accept = service.AcceptOnThreadPoolAsync();
        TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, service.Port);
        ServerConnection connection = await accept.WaitAsync(Deadline);
        Assert.Equal(1, service.ConnectionCount);

        // Bytes the client never reads: they sit in its receive buffer when it closes.
        connection.Enqueue(new ServerActionMessage(new AuthoritativeActionResult("session-a", 1, 1, true, null)));
        Assert.True(await connection.FlushOutboundAsync(Deadline));
        await WaitUntilAsync(() => client.Available > 0, "the server's bytes to reach the client");
        client.Dispose();

        await WaitUntilAsync(() => service.ConnectionCount == 0, "the reset connection to be released");
        lock (errors)
        {
            Assert.Empty(errors);
        }
    }

    // The case the tester saw: a joined RemoteClientSession disposed while the server keeps
    // streaming snapshots to it. Whether the close reaches the server as a FIN or as a reset, it
    // is no connection error.
    [Fact]
    public async Task ClientSessionDisposedMidStream_RaisesNoConnectionError()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        await using DedicatedServerService service = new(
            host,
            new TcpServerTransport(IPAddress.Loopback, 0),
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            IntegratedLocalServerHandle.DefaultConnectionOptions);
        service.Start();
        List<ServerConnectionError> errors = [];
        service.ConnectionError += error =>
        {
            lock (errors)
            {
                errors.Add(error);
            }
        };

        using CancellationTokenSource stopTicks = new();
        Task tickPump = PumpTicksAsync(service, stopTicks.Token);
        try
        {
            Task<ServerConnection> accept = service.AcceptOnThreadPoolAsync();
            RemoteClientSession session = await RemoteClientSession.ConnectAsync(
                new RemoteClientOptions("127.0.0.1", service.Port, "reset-mid-stream", "reset-mid-stream", "reset-test"));
            await accept.WaitAsync(Deadline);
            try
            {
                await WaitUntilAsync(() => session.IsJoined, "the join");
                long framesAtJoin = session.InboundFrameCount;
                await WaitUntilAsync(() => session.InboundFrameCount > framesAtJoin + 3, "snapshots");
            }
            finally
            {
                // Mid-stream: the server is still ticking and sending snapshots.
                await session.DisposeAsync();
            }

            await WaitUntilAsync(() => service.ConnectionCount == 0, "the closed connection to be released");
        }
        finally
        {
            stopTicks.Cancel();
            await tickPump;
        }

        lock (errors)
        {
            Assert.True(errors.Count == 0, string.Join("; ", errors.Select(error => $"{error.Kind}: {error.Reason}")));
        }
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
