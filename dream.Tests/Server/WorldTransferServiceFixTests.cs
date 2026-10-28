using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Multiplayer.Protocol;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Server;

// Wave 10 fix loop 1, server side:
//  - tester B / reviewer S6: an overflow resync waits while the connection's world transfer is
//    queued or going out, instead of queueing another full world behind it;
//  - tester C: world chunks are sized from the send-stall allowance, so a 30 KB/s link completes
//    a resync (256 KB chunks faulted it);
//  - reviewer S4 (NETR-8): a taken-over connection's mark is forgotten when its loop ends.
public sealed class WorldTransferServiceFixTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OverflowResync_WhileATransferIsInFlight_IsDeferred_ThenServedOnceAfterIt()
    {
        MutatingAuthority authority = new() { MutationsPerTick = 3 };
        DedicatedServerHost host = CreateHost(authority);
        ServerConnectionOptions options = WithoutLiveness with { MaxCoalescedWorldMutations = 8, SnapshotIntervalTicks = 1 };
        await using DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport transport = new("in-flight");
        ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        service.AttachConnection(connection);
        host.HandleClientMessage("in-flight", new ClientJoinRequestMessage("session-a", "a"));
        Assert.False(connection.HasWorldTransferPending);

        // A world transfer whose carrier write is blocked.
        transport.Close();
        WorldSnapshot world = WorldTransferProtocolTests.CreateWorld(cubes: 200, rubble: 4, subcells: 1);
        connection.Enqueue(new ServerSnapshotMessage(CreateSnapshot(1, new Dictionary<string, ulong>(), []) with { World = world }));
        Assert.True(await transport.SendStarted.WaitAsync(Deadline));
        Assert.True(connection.HasWorldTransferPending);

        // Snapshots pile up behind it and overflow the merge cap: the resync is deferred, not served.
        for (int tick = 0; tick < 12; tick++)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        }

        Assert.True(connection.OverflowResyncs >= 1);
        Assert.Equal(0, authority.WorldResyncs);
        ServerMetricsSnapshot deferred = service.CaptureMetrics();
        Assert.Equal(0, deferred.CoalesceOverflowResyncs);
        Assert.Equal(1, deferred.CoalesceOverflowResyncsDeferred);

        // The transfer finishes; the next broadcast serves one resync, counted as back to back.
        transport.Open();
        Assert.True(await connection.FlushOutboundAsync(Deadline));
        Assert.False(connection.HasWorldTransferPending);
        for (int tick = 0; tick < 3 && authority.WorldResyncs == 0; tick++)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        }

        Assert.True(await connection.FlushOutboundAsync(Deadline));
        Assert.Equal(1, authority.WorldResyncs);
        ServerMetricsSnapshot served = service.CaptureMetrics();
        Assert.Equal(1, served.CoalesceOverflowResyncs);
        Assert.Equal(1, served.BackToBackOverflowResyncs);
        Assert.Equal(0, served.BackToBackWorldTransfers);
        Assert.Equal(2, connection.WorldTransfers);

        // On the wire: two transfers, one after the other, each complete.
        IReadOnlyList<IProtocolMessage> frames = Decode(transport.Frames);
        List<IServerToClientMessage> delivered = WorldTransferReassembler.Reassemble(frames);
        Assert.Equal(2, delivered.Count(message => message is ServerSnapshotMessage { Snapshot.World: not null }));
    }

    [Fact]
    public void WorldTransfers_QueuedBehindAnother_AreCountedAsBackToBack()
    {
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        GatedTransport transport = new("back-to-back");
        transport.Close();
        ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);
        try
        {
            WorldSnapshot world = WorldTransferProtocolTests.CreateWorld(cubes: 50, rubble: 1, subcells: 0);
            connection.Enqueue(new ServerSnapshotMessage(CreateSnapshot(1, new Dictionary<string, ulong>(), []) with { World = world }));
            Assert.Equal(0, connection.BackToBackWorldTransfers);
            connection.Enqueue(new ServerSnapshotMessage(CreateSnapshot(2, new Dictionary<string, ulong>(), []) with { World = world }));
            Assert.Equal(1, connection.BackToBackWorldTransfers);
            // A snapshot without a world is no transfer.
            connection.EnqueueSnapshot(new ServerSnapshotMessage(CreateSnapshot(3, new Dictionary<string, ulong>(), [])));
            Assert.Equal(1, connection.BackToBackWorldTransfers);
            Assert.True(connection.HasWorldTransferPending);
        }
        finally
        {
            transport.Open();
            Assert.True(connection.FlushOutboundAsync(Deadline).AsTask().GetAwaiter().GetResult());
            Assert.False(connection.HasWorldTransferPending);
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
            host.Dispose();
        }
    }

    // Tester C: a 30 KB/s link. The clock is virtual: each 2 KB slice of a write advances it by the
    // slice's time on the link, and the service's liveness check runs after every slice, as the
    // tick thread would. With the default 64 KB chunks every frame finishes inside its stall
    // allowance (5 s plus its size at 256 KB/s) and the resync completes; with the earlier 256 KB
    // chunks the first chunk overruns it (8.5 s against 6 s) and the connection faults mid-resync.
    [Theory]
    [InlineData(WorldChunkMessage.DefaultChunkBytes, false)]
    [InlineData(256 * 1024, true)]
    public async Task Resync_OverA30KBpsLink_CompletesWithTheDefaultChunks(int chunkBytes, bool expectStall)
    {
        Assert.Equal(64 * 1024, WorldChunkMessage.DefaultChunkBytes);
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = new()
        {
            IdleTimeout = TimeSpan.Zero,
            SendStallTimeout = TimeSpan.FromSeconds(5),
            TimeProvider = clock
        };
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        VirtualLinkTransport transport = new("link-30k", clock, bytesPerSecond: 30 * 1024);
        await using ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options)
        {
            WorldChunkBytes = chunkBytes
        };
        transport.Connection = connection;
        try
        {
            WorldSnapshot world = WorldTransferProtocolTests.CreateWorld(cubes: 50_000, rubble: 1_000, subcells: 10);
            connection.Enqueue(new ServerSnapshotMessage(CreateSnapshot(1, new Dictionary<string, ulong>(), []) with { World = world }));
            for (ulong tick = 2; tick < 6; tick++)
            {
                connection.EnqueueSnapshot(new ServerSnapshotMessage(CreateSnapshot(tick, new Dictionary<string, ulong>(), [])));
            }

            bool flushed = await connection.FlushOutboundAsync(TimeSpan.FromSeconds(30));
            double linkSeconds = clock.GetTimestamp() / (double)clock.TimestampFrequency;
            output.WriteLine(
                $"{chunkBytes / 1024} KB chunks: {connection.WorldChunksSent} chunks, {connection.BytesSent:N0} B over {linkSeconds:0.0} s of link time; " +
                $"longest write {transport.LongestWriteSeconds:0.00} s; faulted {connection.IsFaulted} ({connection.CloseReason})");
            if (expectStall)
            {
                Assert.True(connection.IsFaulted);
                Assert.Equal(ServerConnectionCloseReason.SendStall, connection.CloseReason);
                return;
            }

            Assert.True(flushed);
            Assert.False(connection.IsFaulted, connection.CloseReason.ToString());
            Assert.True(transport.LongestWriteSeconds < 5.25, $"a write took {transport.LongestWriteSeconds:0.00} s");
            List<IServerToClientMessage> delivered = WorldTransferReassembler.Reassemble(Decode(transport.Frames));
            WorldSnapshot received = Assert.IsType<ServerSnapshotMessage>(delivered[0]).Snapshot.World!.Value;
            Assert.Equal(world.PlacedCubes!.Count, received.PlacedCubes!.Count);
            Assert.Equal(world.FracturedCubes!.Count, received.FracturedCubes!.Count);
            // The four snapshots queued behind the world merged while it went out; the newest is last.
            Assert.Equal(5UL, Assert.IsType<ServerSnapshotMessage>(delivered[^1]).Snapshot.ServerTick);
        }
        finally
        {
            host.Dispose();
        }
    }

    // Reviewer S4 (NETR-8): the host marks a taken-over connection so its late messages cannot
    // come back as a ghost. The mark used to stay until a new socket reused the id, so the set
    // grew with every takeover; the service now forgets it when the connection's loop ends.
    [Fact]
    public async Task TakenOverConnection_IsForgotten_WhenItsLoopEnds()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = new()
        {
            IdleTimeout = TimeSpan.FromSeconds(10),
            SendStallTimeout = TimeSpan.FromSeconds(5),
            TimeProvider = clock
        };
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: true);
        QueuedServerTransport listener = new();
        DedicatedServerService service = new(host, listener, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport oldTransport = new("connection-old", recordFrames: false);
        GatedTransport newTransport = new("connection-new", recordFrames: false);
        listener.Offer(oldTransport);
        listener.Offer(newTransport);
        try
        {
            ServerConnection oldConnection = await service.AcceptNextClientAsync();
            await service.AcceptNextClientAsync();
            host.HandleClientMessage("connection-old", new ClientJoinRequestMessage("session-a", "Player A"));
            await AdvanceUntilAsync(service, () => host.IsConnectionJoined("connection-old"));

            clock.Advance(TimeSpan.FromSeconds(2.5));
            host.HandleClientMessage("connection-new", new ClientJoinRequestMessage("session-a", "Player A"));
            await AdvanceUntilAsync(service, () => service.ConnectionCount == 1);
            Assert.True(oldTransport.IsDisposed);
            Assert.True(host.IsConnectionJoined("connection-new"));

            // The old loop sees its transport closed and ends; its mark goes with it.
            DateTime deadline = DateTime.UtcNow + Deadline;
            while (TakenOverIds(host).Count > 0)
            {
                Assert.True(DateTime.UtcNow < deadline, $"still marked: {string.Join(", ", TakenOverIds(host).Keys)}");
                await Task.Delay(5);
            }

            Assert.Empty(TakenOverIds(host));
            Assert.True(oldConnection.IsFaulted || oldTransport.IsDisposed);
            Assert.True(host.IsConnectionJoined("connection-new"));
            Assert.False(host.TryGetConnectionContext("connection-old", out _));
        }
        finally
        {
            await service.DisposeAsync();
            host.Dispose();
        }
    }

    private static ConcurrentDictionary<string, byte> TakenOverIds(DedicatedServerHost host)
    {
        FieldInfo field = typeof(DedicatedServerHost).GetField("_takenOverConnections", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DedicatedServerHost._takenOverConnections not found");
        return (ConcurrentDictionary<string, byte>)field.GetValue(host)!;
    }

    private static async Task AdvanceUntilAsync(DedicatedServerService service, Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            await Task.Delay(2);
        }

        Assert.True(condition());
    }

    private sealed class QueuedServerTransport : IServerTransport
    {
        private readonly Channel<ITransportConnection> _pending = Channel.CreateUnbounded<ITransportConnection>();

        public int Port => 0;

        public void Offer(ITransportConnection connection) => _pending.Writer.TryWrite(connection);

        public void Start()
        {
        }

        public ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default)
        {
            return _pending.Reader.ReadAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _pending.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    // A link of bytesPerSecond on a virtual clock: each write advances the clock by its time on
    // the link in 2 KB slices and runs the connection's liveness check after each slice.
    private sealed class VirtualLinkTransport(string connectionId, ManualTimeProvider clock, int bytesPerSecond) : ITransportConnection
    {
        private const int SliceBytes = 2048;
        private readonly object _sync = new();
        private readonly List<byte[]> _frames = [];
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ServerConnection? Connection { get; set; }

        public double LongestWriteSeconds { get; private set; }

        public string ConnectionId => connectionId;

        public byte[][] Frames
        {
            get
            {
                lock (_sync)
                {
                    return [.. _frames];
                }
            }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            double seconds = 0;
            for (int offset = 0; offset < payload.Length; offset += SliceBytes)
            {
                int length = Math.Min(SliceBytes, payload.Length - offset);
                TimeSpan slice = TimeSpan.FromSeconds((double)length / bytesPerSecond);
                clock.Advance(slice);
                seconds += slice.TotalSeconds;
                if (Connection?.CheckLiveness() == true)
                {
                    LongestWriteSeconds = Math.Max(LongestWriteSeconds, seconds);
                    throw new IOException("the link was closed for a send stall");
                }
            }

            LongestWriteSeconds = Math.Max(LongestWriteSeconds, seconds);
            lock (_sync)
            {
                _frames.Add(payload.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _disposed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public ValueTask DisposeAsync()
        {
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
