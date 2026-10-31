using System.Text;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.World;
using Dream.Tests.Multiplayer.Protocol;
using static Dream.Tests.Multiplayer.Protocol.TerrainTransferTestData;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Server;

// Protocol v8: the server end of terrain transfer, through a real ServerConnection and a recording transport.
public sealed class ServerConnectionTerrainTransferTests
{
    private static readonly TransportJsonCodec Codec = new();

    private static ServerConnection CreateConnection(Dream.Multiplayer.Transport.ITransportConnection transport)
    {
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        return new ServerConnection(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);
    }

    private static string KindOf(byte[] frame)
    {
        Assert.True(new LengthPrefixedMessageFramer(1 << 26).TryDeframe(frame, out byte[]? payload, out _));
        return Codec.Decode(payload!).Envelope.MessageKind;
    }

    private static WorldSnapshot TinyWorld() => WorldSnapshotCapture.Rent(2, 2, 2, null, 0, 0, 0, 0).ToWorldSnapshot();

    [Fact]
    public async Task StoredJoin_SendsCarrier_ThenTerrain_ThenWorld_WithQueuedMessagesInterleaved()
    {
        TerrainTransferPayload payload = Payload();
        Assert.True(payload.Fragments.Count >= 3);
        GatedTransport transport = new("stored-join");
        await using ServerConnection connection = CreateConnection(transport);

        // The first write blocks, so everything is queued before the writer moves on.
        transport.Close();
        connection.Enqueue(Carrier(payload, null, TinyWorld()));
        ServerHelloMessage[] queued = Enumerable.Range(0, 4).Select(i => new ServerHelloMessage($"queued-{i}", 8, (ulong)i)).ToArray();
        foreach (ServerHelloMessage message in queued)
        {
            connection.Enqueue(message);
        }

        await transport.SendStarted.WaitAsync(Deadline);
        transport.Open();
        Assert.True(await connection.FlushOutboundAsync(Deadline));

        byte[][] frames = transport.Frames;
        string[] kinds = frames.Select(KindOf).ToArray();
        Assert.Equal("server.join-accepted", kinds[0]);
        Assert.Equal(TerrainChunkMessage.Kind, kinds[1]);
        Assert.Equal(WorldChunkMessage.Kind, kinds[^1]);
        // Terrain frames come in order, all before the world chunk, and the queued messages interleave.
        int[] terrainAt = Enumerable.Range(0, kinds.Length).Where(i => kinds[i] == TerrainChunkMessage.Kind).ToArray();
        Assert.Equal(payload.Fragments.Count, terrainAt.Length);
        Assert.True(terrainAt[^1] < Array.IndexOf(kinds, WorldChunkMessage.Kind));
        Assert.Contains(kinds.Take(terrainAt[^1]), kind => kind == "server.hello");
        Assert.Equal(queued.Length, kinds.Count(kind => kind == "server.hello"));
        Assert.Equal(1 + payload.Fragments.Count + queued.Length + 1, frames.Length);

        Assert.Equal(1, connection.WorldTransfers);
        Assert.Equal(payload.Fragments.Count, connection.TerrainFragmentsSent);
        Assert.Equal(1, connection.WorldChunksSent);

        // The client side sees the join with its terrain, then the queued messages in order.
        IReadOnlyList<IProtocolMessage> wire = Decode(frames);
        TerrainChunkMessage[] terrain = wire.OfType<TerrainChunkMessage>().ToArray();
        Assert.Equal(Enumerable.Range(0, payload.Fragments.Count), terrain.Select(fragment => fragment.Index));
        Assert.All(terrain, fragment => Assert.Equal(wire.OfType<JoinAcceptedMessage>().Single().WorldTransferId, fragment.TransferId));
        Assert.Equal([false, .. Enumerable.Repeat(false, terrain.Length - 2), true], terrain.Select(fragment => fragment.IsFinal));

        List<IServerToClientMessage> delivered = WorldTransferReassembler.Reassemble(wire);
        JoinAcceptedMessage join = Assert.IsType<JoinAcceptedMessage>(delivered[0]);
        Assert.Null(join.WorldTransferId);
        Assert.NotNull(join.ReceivedTerrain);
        Assert.Equal(payload.Chunks.Count, join.ReceivedTerrain.Count);
        for (int i = 0; i < payload.Chunks.Count; i++)
        {
            Assert.Equal(payload.Chunks[i].ChunkX, join.ReceivedTerrain[i].ChunkX);
            Assert.Equal(payload.Chunks[i].ChunkZ, join.ReceivedTerrain[i].ChunkZ);
            Assert.Equal(payload.Chunks[i].FormatVersion, join.ReceivedTerrain[i].FormatVersion);
            Assert.Equal(payload.Chunks[i].CellCount, join.ReceivedTerrain[i].CellCount);
            Assert.Equal(payload.Chunks[i].Data, join.ReceivedTerrain[i].Data);
        }

        Assert.Equal(queued.Select(m => m.ServerName), delivered.Skip(1).OfType<ServerHelloMessage>().Select(m => m.ServerName));
    }

    [Fact]
    public async Task StoredJoin_WithNothingQueued_StreamsTerrainBackToBack()
    {
        TerrainTransferPayload payload = Payload();
        GatedTransport transport = new("stored-join-alone");
        await using ServerConnection connection = CreateConnection(transport);

        connection.Enqueue(Carrier(payload, null, TinyWorld()));
        Assert.True(await connection.FlushOutboundAsync(Deadline));

        string[] kinds = transport.Frames.Select(KindOf).ToArray();
        Assert.Equal(
            ["server.join-accepted", .. Enumerable.Repeat(TerrainChunkMessage.Kind, payload.Fragments.Count), WorldChunkMessage.Kind],
            kinds);
    }

    // A carrier without a payload is exactly today's two frames, byte for byte.
    [Fact]
    public async Task JoinWithoutPayload_ProducesExactlyTodaysFrames_AndBytes()
    {
        WorldSnapshot world = TinyWorld();
        JoinAcceptedMessage join = new("session-a", 1, 2, world, World: WorldDescriptorWire.From(World()));
        GatedTransport transport = new("generated-join");
        await using ServerConnection connection = CreateConnection(transport);

        connection.Enqueue(join);
        Assert.True(await connection.FlushOutboundAsync(Deadline));

        LengthPrefixedMessageFramer framer = new(1 << 26);
        JoinAcceptedMessage carrier = join with { InitialWorld = null, WorldTransferId = 1 };
        byte[] expectedCarrier = framer.Frame(Codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "server.join-accepted", 1), carrier));
        byte[] expectedChunk = framer.Frame(Codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, WorldChunkMessage.Kind, 2),
            new WorldChunkMessage(1, 0, true, TinyWorldSnapshotForCompare())));

        byte[][] frames = transport.Frames;
        Assert.Equal(2, frames.Length);
        Assert.Equal(expectedCarrier, frames[0]);
        Assert.Equal(expectedChunk, frames[1]);
        Assert.DoesNotContain("terrain", Encoding.UTF8.GetString(frames[0]));
        Assert.Equal(0, connection.TerrainFragmentsSent);
        Assert.Equal(1, connection.WorldChunksSent);
    }

    [Fact]
    public async Task JoinWithoutPayload_AndNoManifest_IsAReassembledV7Join()
    {
        JoinAcceptedMessage join = new("session-a", 1, 2, TinyWorld(), World: WorldDescriptorWire.From(World()));
        GatedTransport transport = new("generated-join-2");
        await using ServerConnection connection = CreateConnection(transport);
        connection.Enqueue(join);
        Assert.True(await connection.FlushOutboundAsync(Deadline));

        List<IServerToClientMessage> delivered = WorldTransferReassembler.Reassemble(Decode(transport.Frames));

        JoinAcceptedMessage received = Assert.IsType<JoinAcceptedMessage>(Assert.Single(delivered));
        Assert.Null(received.Terrain);
        Assert.Null(received.ReceivedTerrain);
        Assert.NotNull(received.InitialWorld);
    }

    [Fact]
    public async Task FaultMidTerrain_ReleasesTheCapture_AndSendsNoMoreTerrain()
    {
        TerrainTransferPayload payload = Payload();
        WorldSnapshotCapture capture = WorldSnapshotCapture.Rent(2, 2, 2, null, 0, 0, 0, 0);
        // The peer's socket breaks on the third write: the carrier and one terrain fragment got through.
        FailingTransport transport = new("fault-mid-terrain", failOnSend: 3);
        await using ServerConnection connection = CreateConnection(transport);
        connection.Enqueue(Carrier(payload, null, capture.ToWorldSnapshot()));

        await WaitUntilAsync(() => connection.IsFaulted);

        // The write loop marks the connection faulted in its catch, then releases the unfinished transfer's
        // capture in its finally: a moment later, on the writer's thread. Under load the test can look in
        // between, so wait for the release (bounded by WaitUntilAsync's deadline) instead of asserting at once.
        await WaitUntilAsync(() => capture.IsReleased);

        Assert.True(capture.IsReleased);
        Assert.Equal(0, connection.WorldChunksSent);
        Assert.Equal(1, connection.TerrainFragmentsSent);
        Assert.Equal(
            ["server.join-accepted", TerrainChunkMessage.Kind],
            transport.Frames.Select(KindOf));
        Assert.False(connection.HasWorldTransferPending);
    }

    [Fact]
    public async Task PayloadThatDoesNotMatchTheManifest_FaultsTheConnection_AndReleasesTheCapture()
    {
        TerrainTransferPayload payload = Payload();
        WorldSnapshotCapture capture = WorldSnapshotCapture.Rent(2, 2, 2, null, 0, 0, 0, 0);
        GatedTransport transport = new("mismatch");
        await using ServerConnection connection = CreateConnection(transport);

        connection.Enqueue(Carrier(payload, null, capture.ToWorldSnapshot()) with { Terrain = payload.Manifest with { Chunks = 4 } });
        await WaitUntilAsync(() => connection.IsFaulted);

        Assert.True(capture.IsReleased);
        Assert.Empty(transport.Frames);
    }

    // Records frames and throws on the failOnSend-th write, like a socket that breaks.
    private sealed class FailingTransport(string connectionId, int failOnSend) : Dream.Multiplayer.Transport.ITransportConnection
    {
        private readonly List<byte[]> _frames = [];
        private int _sends;

        public string ConnectionId { get; } = connectionId;

        public byte[][] Frames
        {
            get
            {
                lock (_frames)
                {
                    return _frames.ToArray();
                }
            }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _sends) >= failOnSend)
            {
                throw new IOException("connection reset");
            }

            lock (_frames)
            {
                _frames.Add(payload.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static WorldSnapshot TinyWorldSnapshotForCompare() => new(2, 2, 2, [], FracturedCubeCells: []);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5);
        }
    }
}
