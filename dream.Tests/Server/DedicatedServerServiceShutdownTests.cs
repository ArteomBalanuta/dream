using System.Diagnostics;
using System.Net;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;

namespace Dream.Tests.Server;

// Wave 8 fix loop: each accepted connection's receive loop used to be started fire-and-forget, and
// its cleanup calls host.ReleaseConnection. RunAsync and DisposeAsync never waited for it, so an
// owner that disposed the host right after them (LoadRunner, IntegratedLocalServerHandle,
// ServerBootstrap) could free the native physics world under a late release. Shutdown now closes
// every connection's transport and awaits the loops before it returns.
public class DedicatedServerServiceShutdownTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task RunAsync_ReturnsOnlyAfterEveryConnectionLoopReleasedItsSeat()
    {
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        List<RemoteClientSession> sessions = [];
        try
        {
            DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer());
            List<ServerConnectionError> errors = [];
            service.ConnectionError += error =>
            {
                lock (errors)
                {
                    errors.Add(error);
                }
            };
            service.Start();
            using CancellationTokenSource shutdown = new();
            Task run = Task.Run(() => service.RunAsync(shutdown.Token));

            for (int i = 0; i < 3; i++)
            {
                RemoteClientSession session = await RemoteClientSession.ConnectAsync(
                    new RemoteClientOptions("127.0.0.1", service.Port, $"shutdown-{i}", $"shutdown-{i}", "shutdown-test"));
                sessions.Add(session);
                await WaitForJoinAsync(session);
            }

            Assert.Equal(3, service.CaptureMetrics().JoinedConnections);
            Assert.Equal(3, service.RunningConnectionLoopCount);

            shutdown.Cancel();
            try
            {
                await run.WaitAsync(Deadline);
            }
            catch (OperationCanceledException)
            {
            }

            // Nothing is left for a late loop to release once RunAsync has returned.
            Assert.Equal(0, service.RunningConnectionLoopCount);
            Assert.Equal(0, service.ConnectionCount);
            Assert.Equal(0, service.CaptureMetrics().JoinedConnections);
            lock (errors)
            {
                Assert.Empty(errors);
            }

            await service.DisposeAsync();
        }
        finally
        {
            foreach (RemoteClientSession session in sessions)
            {
                await session.DisposeAsync();
            }

            host.Dispose();
        }
    }

    [Fact]
    public async Task DisposeAsync_WithoutRunAsync_ClosesAndAwaitsTheConnectionLoops_AndReleasesTheSeats()
    {
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        RemoteClientSession? session = null;
        try
        {
            DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer());
            List<ServerConnectionError> errors = [];
            service.ConnectionError += error =>
            {
                lock (errors)
                {
                    errors.Add(error);
                }
            };
            service.Start();
            Task<ServerConnection> accept = service.AcceptNextClientAsync().AsTask();
            session = await RemoteClientSession.ConnectAsync(
                new RemoteClientOptions("127.0.0.1", service.Port, "dispose-a", "dispose-a", "shutdown-test"));
            ServerConnection connection = await accept.WaitAsync(Deadline);
            Stopwatch clock = Stopwatch.StartNew();
            while (!host.IsConnectionJoined(connection.ConnectionId))
            {
                Assert.True(clock.Elapsed < Deadline, "the connection never joined");
                await Task.Delay(5);
            }

            Assert.Equal(1, service.RunningConnectionLoopCount);

            await service.DisposeAsync().AsTask().WaitAsync(Deadline);

            Assert.Equal(0, service.RunningConnectionLoopCount);
            Assert.False(host.IsConnectionJoined(connection.ConnectionId));
            Assert.Equal(0, service.ConnectionCount);
            lock (errors)
            {
                Assert.Empty(errors);
            }

            // The client sees the close, with a reason.
            clock.Restart();
            while (session.IsConnected)
            {
                Assert.True(clock.Elapsed < Deadline, "the client never saw the close");
                await Task.Delay(5);
            }

            Assert.StartsWith("Connection lost", session.DisconnectReason);
        }
        finally
        {
            if (session != null)
            {
                await session.DisposeAsync();
            }

            host.Dispose();
        }
    }

    // Reviewer nit: a faulted connection's transport is closed early for every close reason, so
    // its pending receive fails. Only idle and send-stall closes were filtered; a backlog overflow
    // surfaced a spurious "error" report on top of the counted close.
    [Fact]
    public async Task BacklogOverflowClose_DoesNotAlsoReportAConnectionError()
    {
        DedicatedServerHost host = ServerConnectionTestFixtures.CreateHost(new ServerConnectionTestFixtures.MutatingAuthority());
        ServerConnectionTestFixtures.GatedTransport transport = new("overflow", recordFrames: false);
        DedicatedServerService service = new(
            host,
            new SingleConnectionServerTransport(transport),
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            ServerConnectionTestFixtures.WithoutLiveness);
        List<ServerConnectionError> errors = [];
        service.ConnectionError += error =>
        {
            lock (errors)
            {
                errors.Add(error);
            }
        };
        try
        {
            ServerConnection connection = await service.AcceptNextClientAsync();
            transport.Close();
            for (int index = 0; index <= ServerConnection.MaxPendingOutboundMessages + 1; index++)
            {
                connection.Enqueue(new ServerActionMessage(new AuthoritativeActionResult("session-a", (ulong)index, 1, true, null)));
            }

            Assert.Equal(ServerConnectionCloseReason.BacklogOverflow, connection.CloseReason);
            await service.AdvanceOnceAsync(Dream.Simulation.Runtime.SimulationClock.FixedDeltaSeconds);
            Stopwatch clock = Stopwatch.StartNew();
            while (service.RunningConnectionLoopCount != 0)
            {
                Assert.True(clock.Elapsed < Deadline, "the connection loop never ended");
                await Task.Delay(5);
            }

            Assert.Equal(1, service.CaptureMetrics().BacklogOverflowCloses);
            lock (errors)
            {
                Assert.Empty(errors);
            }
        }
        finally
        {
            transport.Open();
            await service.DisposeAsync();
            host.Dispose();
        }
    }

    private static async Task WaitForJoinAsync(RemoteClientSession session)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < Deadline)
        {
            while (session.TryDequeueInbound(out IServerToClientMessage? message))
            {
                if (message is JoinAcceptedMessage)
                {
                    return;
                }
            }

            await Task.Delay(5);
        }

        Assert.Fail("join was never accepted");
    }

    private sealed class SingleConnectionServerTransport(ITransportConnection connection) : IServerTransport
    {
        private ITransportConnection? _next = connection;

        public int Port => 5058;

        public void Start()
        {
        }

        public ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default)
        {
            ITransportConnection? next = Interlocked.Exchange(ref _next, null);
            return next != null
                ? ValueTask.FromResult(next)
                : ValueTask.FromException<ITransportConnection>(new InvalidOperationException("only one connection"));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
