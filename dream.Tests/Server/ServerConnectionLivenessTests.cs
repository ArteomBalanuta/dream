using System.Diagnostics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Runtime;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Server;

// NETR-1: a client that stops sending, or stops reading, used to keep its seat until the OS
// noticed. The service now closes it after the idle or send-stall timeout, through the same
// release path as any faulted connection, and counts the close.
public class ServerConnectionLivenessTests
{
    private static ServerConnectionOptions Options(ManualTimeProvider clock) => new()
    {
        IdleTimeout = TimeSpan.FromSeconds(10),
        SendStallTimeout = TimeSpan.FromSeconds(5),
        TimeProvider = clock,
        // One snapshot per manual tick, as these tests count writes per tick.
        SnapshotIntervalTicks = 1
    };

    [Fact]
    public void Options_DefaultToTenSecondsIdleAndFiveSecondsSendStall()
    {
        ServerConnectionOptions defaults = ServerConnectionOptions.Default;

        Assert.Equal(TimeSpan.FromSeconds(10), defaults.IdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), defaults.SendStallTimeout);
        Assert.Equal(4096, defaults.MaxCoalescedWorldMutations);
        Assert.Same(TimeProvider.System, defaults.TimeProvider);
    }

    [Fact]
    public async Task IdleConnection_IsReleasedAfterTheIdleTimeout_AndInboundFramesKeepItAlive()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = Options(clock);
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport transport = new("idle", recordFrames: false);
        ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        service.AttachConnection(connection);
        host.HandleClientMessage("idle", new ClientJoinRequestMessage("session-a", "a"));
        Assert.True(host.IsConnectionJoined("idle"));

        clock.Advance(TimeSpan.FromSeconds(9));
        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        Assert.Equal(1, service.ConnectionCount);

        // An empty frame is a complete inbound frame: it restarts the idle timer.
        transport.Feed(new LengthPrefixedMessageFramer().Frame(ReadOnlySpan<byte>.Empty));
        Assert.True(await connection.ProcessIncomingAsync());
        clock.Advance(TimeSpan.FromSeconds(9));
        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        Assert.Equal(1, service.ConnectionCount);
        Assert.False(connection.IsFaulted);

        clock.Advance(TimeSpan.FromSeconds(1.5));
        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);

        Assert.Equal(0, service.ConnectionCount);
        Assert.True(connection.IsFaulted);
        Assert.Equal(ServerConnectionCloseReason.IdleTimeout, connection.CloseReason);
        Assert.False(host.IsConnectionJoined("idle"));
        Assert.True(transport.IsDisposed);
        ServerMetricsSnapshot metrics = service.CaptureMetrics();
        Assert.Equal(1, metrics.IdleTimeoutCloses);
        Assert.Equal(0, metrics.SendStallCloses);
        Assert.Equal(0, metrics.BacklogOverflowCloses);
    }

    [Fact]
    public async Task ReleasedIdleSeat_CanBeTakenAgainByANewConnection()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = Options(clock);
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        service.AttachConnection(new ServerConnection(new GatedTransport("old", recordFrames: false), host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options));
        host.HandleClientMessage("old", new ClientJoinRequestMessage("session-a", "a"));

        clock.Advance(TimeSpan.FromSeconds(11));
        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        Assert.Equal(0, service.ConnectionCount);

        service.AttachConnection(new ServerConnection(new GatedTransport("new", recordFrames: false), host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options));
        IReadOnlyList<IServerToClientMessage> replies = host.HandleClientMessage("new", new ClientJoinRequestMessage("session-a", "a"));
        Assert.Contains(replies, reply => reply is JoinAcceptedMessage);
        Assert.True(host.IsConnectionJoined("new"));
    }

    [Fact]
    public async Task StalledSend_ClosesTheConnection_WithoutHoldingUpTheTick()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = Options(clock);
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport transport = new("stalled");
        ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        service.AttachConnection(connection);
        host.HandleClientMessage("stalled", new ClientJoinRequestMessage("session-a", "a"));
        // The gate ignores cancellation: only closing the transport ends the write.
        transport.Close();

        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        Assert.True(await transport.SendStarted.WaitAsync(Deadline));
        clock.Advance(TimeSpan.FromSeconds(4));
        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        Assert.Equal(1, service.ConnectionCount);
        Assert.False(connection.IsFaulted);

        clock.Advance(TimeSpan.FromSeconds(2));
        Stopwatch elapsed = Stopwatch.StartNew();
        Task<int> advance = service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds).AsTask();
        Assert.True(advance.Wait(Deadline), "Dropping a stalled connection blocked the tick.");
        elapsed.Stop();

        Assert.Equal(0, service.ConnectionCount);
        Assert.Equal(ServerConnectionCloseReason.SendStall, connection.CloseReason);
        Assert.False(host.IsConnectionJoined("stalled"));
        Assert.True(transport.IsDisposed);
        ServerMetricsSnapshot metrics = service.CaptureMetrics();
        Assert.Equal(1, metrics.SendStallCloses);
        Assert.Equal(0, metrics.IdleTimeoutCloses);
    }

    [Fact]
    public async Task SlowButProgressingSends_DoNotCountAsAStall()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = Options(clock);
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport transport = new("slow", recordFrames: false);
        ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        service.AttachConnection(connection);
        host.HandleClientMessage("slow", new ClientJoinRequestMessage("session-a", "a"));

        // Each write takes 3 s of clock time, but every write completes: 15 s pass in total.
        for (int i = 0; i < 5; i++)
        {
            transport.Close();
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            Assert.True(await transport.SendStarted.WaitAsync(Deadline));
            clock.Advance(TimeSpan.FromSeconds(3));
            transport.Open();
            Assert.True(await connection.FlushOutboundAsync(Deadline));
            // Inbound frames keep arriving, so only the send side is under test.
            transport.Feed(new LengthPrefixedMessageFramer().Frame(ReadOnlySpan<byte>.Empty));
            Assert.True(await connection.ProcessIncomingAsync());
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        }

        Assert.Equal(1, service.ConnectionCount);
        Assert.False(connection.IsFaulted);
    }

    [Fact]
    public async Task ZeroTimeouts_TurnTheLivenessChecksOff()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = new()
        {
            IdleTimeout = TimeSpan.Zero,
            SendStallTimeout = TimeSpan.Zero,
            TimeProvider = clock,
            SnapshotIntervalTicks = 1
        };
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport transport = new("quiet");
        ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        service.AttachConnection(connection);
        host.HandleClientMessage("quiet", new ClientJoinRequestMessage("session-a", "a"));
        transport.Close();

        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        Assert.True(await transport.SendStarted.WaitAsync(Deadline));
        clock.Advance(TimeSpan.FromHours(1));
        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);

        Assert.Equal(1, service.ConnectionCount);
        Assert.False(connection.CheckLiveness());
        transport.Open();
    }

    [Fact]
    public async Task ExistingConstructors_UseTheDefaultOptions()
    {
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer());
        await using ServerConnection connection = new(new GatedTransport("plain"), host, new TransportJsonCodec(), new LengthPrefixedMessageFramer());

        Assert.Same(ServerConnectionOptions.Default, service.ConnectionOptions);
        Assert.Same(ServerConnectionOptions.Default, connection.Options);
        Assert.False(connection.CheckLiveness());
    }

    [Fact]
    public async Task BacklogOverflowClose_IsCounted()
    {
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer());
        GatedTransport transport = new("backlog", recordFrames: false);
        transport.Close();
        ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer());
        service.AttachConnection(connection);

        for (int index = 0; index <= ServerConnection.MaxPendingOutboundMessages + 1; index++)
        {
            connection.Enqueue(new ServerActionMessage(new AuthoritativeActionResult("session-a", (ulong)index, 1, true, null)));
        }

        Assert.Equal(ServerConnectionCloseReason.BacklogOverflow, connection.CloseReason);
        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);

        Assert.Equal(0, service.ConnectionCount);
        Assert.Equal(1, service.CaptureMetrics().BacklogOverflowCloses);
        Assert.Equal(1, service.TickMetrics.BacklogOverflowCloses);
    }

    [Fact]
    public void LivenessCheck_AllocatesNothingWhileTheConnectionIsLive()
    {
        ManualTimeProvider clock = new();
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        ServerConnection connection = new(new GatedTransport("live"), host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), Options(clock));

        Dream.Tests.Utilities.AllocationAssert.AtMost(0, () => connection.CheckLiveness());
        Assert.False(connection.IsFaulted);
    }
}
