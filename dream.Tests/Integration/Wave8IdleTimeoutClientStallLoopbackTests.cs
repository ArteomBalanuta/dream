using System.Diagnostics;
using System.Net;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Integration;

// Wave 8 tester, updated by the wave 8 fix loop: NETR-1's idle timeout (ServerConnectionOptions.IdleTimeout,
// 10 s, no inbound frame) against a real client that sends commands only from prediction ticks.
//
// - Menus: PlayScene.Update keeps running with gameplay input off and still produces one (idle)
//   command per prediction tick (PlayScene.cs:186-206), so an open menu is never idle.
// - A frozen game loop (a long GC, a debugger break, a Windows modal move/resize loop while the
//   title bar is held) sends no commands. The session's receive task keeps draining the socket, so
//   the server never sees a send stall, and since NETR-2 the session's own keepalive task sends an
//   empty frame whenever nothing went out for RemoteClientSession.KeepaliveInterval (1 s), so the
//   idle clock never runs out either. Measured against the dedicated server's default options:
//   3 s, 8 s and 11 s stalls all stay connected.
// - A truly dead client (its frames no longer reach the server) is still closed for idle, and the
//   client side then reports a disconnect reason instead of null.
// - The integrated local server turns the liveness checks off altogether: its client lives in the
//   same process, and a debugger break pauses the server's tick too.
public class Wave8IdleTimeoutClientStallLoopbackTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ClientLoopStall_ThreeSeconds_StaysConnected()
    {
        StallOutcome outcome = await RunAsync(TimeSpan.FromSeconds(3), "w8-idle-3");
        output.WriteLine(outcome.ToString());
        Assert.True(outcome.ConnectedAfter, outcome.ToString());
        Assert.True(outcome.SnapshotsAfterResume > 0, outcome.ToString());
    }

    [Fact]
    public async Task ClientLoopStall_EightSeconds_StaysConnectedUnderTheTenSecondIdleTimeout()
    {
        StallOutcome outcome = await RunAsync(TimeSpan.FromSeconds(8), "w8-idle-8");
        output.WriteLine(outcome.ToString());
        Assert.True(outcome.ConnectedAfter, outcome.ToString());
        Assert.True(outcome.SnapshotsAfterResume > 0, outcome.ToString());
    }

    // Was "IsKickedForIdleTimeout" before NETR-2: past the 10 s idle timeout, the session's
    // keepalive (sent from its own task, not the frozen loop) keeps the connection alive.
    [Fact]
    public async Task ClientLoopStall_ElevenSeconds_StaysConnectedThroughTheSessionKeepalive()
    {
        StallOutcome outcome = await RunAsync(TimeSpan.FromSeconds(11), "w8-idle-11");
        output.WriteLine(outcome.ToString());
        Assert.True(outcome.ConnectedAfter, outcome.ToString());
        Assert.Null(outcome.DisconnectReason);
        Assert.True(outcome.SnapshotsAfterResume > 0, outcome.ToString());
        // About one per second of the stall; at least half of them, to allow for a loaded machine.
        Assert.True(outcome.KeepalivesDuringStall >= 5, outcome.ToString());
    }

    // A client whose frames stop reaching the server (a hung process, a dead link) is still closed
    // for idle with the keepalive in place, and the session reports why it lost the connection.
    [Fact]
    public async Task DeadClient_IsStillClosedForIdle_AndTheSessionReportsAReason()
    {
        ServerConnectionOptions options = new()
        {
            IdleTimeout = TimeSpan.FromSeconds(2),
            SendStallTimeout = TimeSpan.Zero
        };
        await using RunningServer server = RunningServer.Start(options);
        FreezableClientTransport transport = new(new TcpClientConnector());
        RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.Port, "w8-dead", "w8-dead", "idle-test"),
            transport);
        await using (session)
        {
            await WaitForJoinAsync(session);
            Assert.Equal(0, server.Service.CaptureMetrics().IdleTimeoutCloses);

            // Freeze right after a keepalive went out, so the server's last inbound frame is the
            // moment of the freeze and the idle clock is measured from there.
            long keepalives = session.SentKeepaliveCount;
            Stopwatch waitForKeepalive = Stopwatch.StartNew();
            while (session.SentKeepaliveCount == keepalives)
            {
                Assert.True(waitForKeepalive.Elapsed < Deadline, "the session never sent a keepalive");
                await Task.Delay(1);
            }

            // From here nothing the client writes reaches the server, keepalives included.
            transport.Connection!.FreezeSends();
            Stopwatch frozen = Stopwatch.StartNew();
            while (session.IsConnected && frozen.Elapsed < Deadline)
            {
                while (session.TryDequeueInbound(out _))
                {
                }

                await Task.Delay(20);
            }

            output.WriteLine($"closed after {frozen.Elapsed.TotalSeconds:0.0} s reason={session.DisconnectReason} keepalives={session.SentKeepaliveCount}");
            Assert.False(session.IsConnected, "the dead client was never closed");
            // Less a little for the delay between the keepalive's write and the freeze.
            Assert.True(frozen.Elapsed >= options.IdleTimeout - TimeSpan.FromMilliseconds(100), $"closed after only {frozen.Elapsed}");
            Assert.NotNull(session.DisconnectReason);
            Assert.StartsWith("Connection lost", session.DisconnectReason);
            // The keepalives were written, they just never arrived.
            Assert.True(session.SentKeepaliveCount > 0);
            ServerMetricsSnapshot metrics = server.Service.CaptureMetrics();
            Assert.Equal(1, metrics.IdleTimeoutCloses);
            Assert.Equal(0, server.Service.ConnectionCount);
        }
    }

    // The same contract, quickly, with a short idle timeout: a session that queues nothing for
    // three idle timeouts stays connected on keepalives alone.
    [Fact]
    public async Task SilentSession_StaysConnectedOnKeepalivesAlone_UnderAShortIdleTimeout()
    {
        ServerConnectionOptions options = new()
        {
            IdleTimeout = TimeSpan.FromSeconds(2.5),
            SendStallTimeout = TimeSpan.Zero
        };
        await using RunningServer server = RunningServer.Start(options);
        RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.Port, "w8-silent", "w8-silent", "idle-test"));
        await using (session)
        {
            await WaitForJoinAsync(session);
            Stopwatch silent = Stopwatch.StartNew();
            while (silent.Elapsed < TimeSpan.FromSeconds(7.5) && session.IsConnected)
            {
                while (session.TryDequeueInbound(out _))
                {
                }

                await Task.Delay(20);
            }

            output.WriteLine($"keepalives={session.SentKeepaliveCount} connected={session.IsConnected} reason={session.DisconnectReason}");
            Assert.True(session.IsConnected, session.DisconnectReason);
            Assert.Null(session.DisconnectReason);
            Assert.True(session.SentKeepaliveCount >= 3, $"keepalives={session.SentKeepaliveCount}");
            Assert.Equal(1, server.Service.ConnectionCount);
            Assert.Equal(0, server.Service.CaptureMetrics().IdleTimeoutCloses);
        }
    }

    [Fact]
    public void IntegratedServer_TurnsTheLivenessChecksOff()
    {
        ServerConnectionOptions options = IntegratedLocalServerHandle.DefaultConnectionOptions;

        Assert.Equal(TimeSpan.Zero, options.IdleTimeout);
        Assert.Equal(TimeSpan.Zero, options.SendStallTimeout);
        // The backlog bounds still apply.
        Assert.Equal(ServerConnectionOptions.Default.MaxCoalescedWorldMutations, options.MaxCoalescedWorldMutations);
        Assert.Equal(ServerConnectionOptions.Default.MaxCoalescedDamageEvents, options.MaxCoalescedDamageEvents);
    }

    // Runs against the dedicated server's default options (10 s idle), not the integrated
    // server's, so the keepalive is what is under test.
    private static async Task<StallOutcome> RunAsync(TimeSpan stall, string sessionId)
    {
        Assert.Equal(TimeSpan.FromSeconds(10), ServerConnectionOptions.Default.IdleTimeout);
        await using IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
            0,
            () => DedicatedServerHost.CreateRuntimeAttached(persistence: null),
            ServerConnectionOptions.Default);
        Assert.True(server.IsReady, server.ErrorMessage);

        RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.BoundPort, sessionId, sessionId, "idle-test"));
        await using (session)
        {
            PlayerSessionId remoteId = new(sessionId);
            ulong tick = 0;
            await WaitForJoinAsync(session);

            // Half a second of normal play: one command per 64 Hz tick.
            await SendTicksAsync(session, remoteId, () => ++tick, TimeSpan.FromMilliseconds(500));

            // The game loop freezes: nothing is queued and nothing is drained.
            long keepalivesBefore = session.SentKeepaliveCount;
            await Task.Delay(stall);
            long keepalivesDuringStall = session.SentKeepaliveCount - keepalivesBefore;

            // Resume; give the server a second to tick, check liveness and answer.
            while (session.TryDequeueInbound(out _))
            {
            }

            int snapshotsAfter = await SendTicksAsync(session, remoteId, () => ++tick, TimeSpan.FromSeconds(1.5));
            return new StallOutcome(stall, session.IsConnected, session.DisconnectReason, snapshotsAfter, keepalivesDuringStall);
        }
    }

    private static async Task WaitForJoinAsync(RemoteClientSession session)
    {
        Stopwatch clock = Stopwatch.StartNew();
        bool joined = false;
        while (!joined && clock.Elapsed < Deadline)
        {
            while (session.TryDequeueInbound(out IServerToClientMessage? message))
            {
                joined |= message is JoinAcceptedMessage;
            }

            await Task.Delay(5);
        }

        Assert.True(joined, "join was never accepted");
    }

    private static async Task<int> SendTicksAsync(RemoteClientSession session, PlayerSessionId remoteId, Func<ulong> nextTick, TimeSpan duration)
    {
        int snapshots = 0;
        Stopwatch clock = Stopwatch.StartNew();
        double sentUntil = 0d;
        while (clock.Elapsed < duration && session.IsConnected)
        {
            while (sentUntil <= clock.Elapsed.TotalSeconds)
            {
                PlayerCommandFrame command = new(
                    remoteId,
                    nextTick(),
                    PlayerModeSwitchIntent.None,
                    PlayerMoveIntent.None,
                    new PlayerLookIntent(0f, 0f, Vector3.UnitZ),
                    PrimaryUsePressed: false);
                session.QueueCommandFrame(RemotePlayerCommandRelay.CreateRelayCommand(command, remoteId, Vector3.UnitZ));
                sentUntil += SimulationClock.FixedDeltaSeconds;
            }

            while (session.TryDequeueInbound(out IServerToClientMessage? message))
            {
                if (message is ServerSnapshotMessage)
                {
                    snapshots++;
                }
            }

            await Task.Delay(5);
        }

        return snapshots;
    }

    private sealed record StallOutcome(TimeSpan Stall, bool ConnectedAfter, string? DisconnectReason, int SnapshotsAfterResume, long KeepalivesDuringStall);

    // A dedicated server on loopback with the given options, run by RunAsync and stopped (with
    // every connection loop awaited) before the host is disposed.
    private sealed class RunningServer : IAsyncDisposable
    {
        private readonly DedicatedServerHost _host;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _run;

        private RunningServer(DedicatedServerHost host, DedicatedServerService service)
        {
            _host = host;
            Service = service;
            _run = Task.Run(() => service.RunAsync(_shutdown.Token));
        }

        public DedicatedServerService Service { get; }

        public int Port => Service.Port;

        public static RunningServer Start(ServerConnectionOptions options)
        {
            DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
            DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                options);
            service.Start();
            return new RunningServer(host, service);
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            try
            {
                await _run;
            }
            catch (OperationCanceledException)
            {
            }

            await Service.DisposeAsync();
            _shutdown.Dispose();
            _host.Dispose();
        }
    }

    private sealed class FreezableClientTransport(IClientTransport inner) : IClientTransport
    {
        public FreezableConnection? Connection { get; private set; }

        public async ValueTask<ITransportConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            Connection = new FreezableConnection(await inner.ConnectAsync(host, port, cancellationToken));
            return Connection;
        }
    }

    // Once frozen, writes complete without reaching the socket: the peer stops hearing from this
    // client while it keeps reading, like a hung process whose socket the OS keeps open.
    private sealed class FreezableConnection(ITransportConnection inner) : ITransportConnection
    {
        private volatile bool _frozen;

        public string ConnectionId => inner.ConnectionId;

        public void FreezeSends() => _frozen = true;

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            return _frozen ? ValueTask.CompletedTask : inner.SendAsync(payload, cancellationToken);
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.ReceiveAsync(buffer, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
