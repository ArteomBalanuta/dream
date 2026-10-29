using System.Net;
using System.Net.Sockets;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Dream.Tests.Multiplayer.Protocol;
using Dream.Tests.Server;

namespace Dream.Tests.Integration;

// Persistence wave E tester: a hostile server over a real loopback socket. Every frame is valid at the
// transport level (the manifest, digest, order and size limits all hold), so the terrain reassembles
// and reaches the client's join applier; what is wrong is the blob content. The client must end the
// session with a protocol error (as PlayScene does when the applier throws InvalidDataException),
// keep the socket open from the server's side, and not allocate without bound.
public sealed class HostileTerrainServerLoopbackTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // One blob over the whole per-world override budget (1,048,576): 17 cells x 64,000 children.
    [Fact]
    public async Task OneBlob_WithMoreSubcellOverridesThanTheBudget_EndsTheSessionAsAProtocolError_WithBoundedMemory()
    {
        Outcome outcome = await RunAsync(chunks => Replace(chunks, 0, 0, HugeBlob(chunks, 0, 0, cellCount: 17)));

        AssertProtocolError(outcome, "subcell override budget exceeded");
        Assert.True(outcome.AllocatedBytes < 300L << 20, $"{outcome.AllocatedBytes:N0} bytes allocated");
    }

    // Each blob alone is within the budget, together they are over it: the budget is per world.
    [Fact]
    public async Task ManyBlobs_EachWithinTheBudget_TogetherOverIt_EndTheSessionAsAProtocolError_WithBoundedMemory()
    {
        Outcome outcome = await RunAsync(chunks =>
        {
            IReadOnlyList<PersistedTerrainChunk> result = chunks;
            for (int chunk = 0; chunk < 4; chunk++)
            {
                result = Replace(result, chunk, 1, HugeBlob(result, chunk, 1, cellCount: 5));
            }

            return result;
        });

        AssertProtocolError(outcome, "subcell override budget exceeded");
        Assert.True(outcome.AllocatedBytes < 300L << 20, $"{outcome.AllocatedBytes:N0} bytes allocated");
    }

    // Just inside the budget (12 x 64,000 = 768,000 plus the stored world's own overrides, under 1,048,576):
    // accepted, because the budget is the bound. What the blob costs the client (decode plus map build) is
    // the allocation over a run of the same world with no hostile blob, whose fixed cost is the map and
    // physics build of the whole world. Measured at the 1<<22 limit: ~965 MB transient, ~188 MB retained.
    [Fact]
    public async Task OneBlob_JustInsideTheBudget_IsAccepted_AndItsCostIsBounded()
    {
        Outcome baseline = await RunAsync(chunks => chunks, expectError: false);
        Outcome outcome = await RunAsync(chunks => Replace(chunks, 0, 0, HugeBlob(chunks, 0, 0, cellCount: 12)), expectError: false);

        long blobBytes = outcome.AllocatedBytes - baseline.AllocatedBytes;
        output.WriteLine($"accepted worst case: {outcome.AllocatedBytes / (1024d * 1024d):N0} MB allocated ({blobBytes / (1024d * 1024d):N0} MB over the {baseline.AllocatedBytes / (1024d * 1024d):N0} MB baseline), {outcome.Milliseconds:N0} ms, {outcome.RetainedBytes / (1024d * 1024d):N0} MB retained");
        Assert.Null(outcome.Error);
        Assert.True(blobBytes < 300L << 20, $"{blobBytes:N0} bytes allocated for the blob");
        Assert.True(outcome.AllocatedBytes < 600L << 20, $"{outcome.AllocatedBytes:N0} bytes allocated in total");
    }

    // The implied-subcell budget (MaxTerrainSubcells): Slope cells at resolution 40 carry no override at
    // all, yet Map walks every implied subcell of each (64,000) for render blocks and collision. Just
    // inside the budget the client accepts them; the cost is bounded because the budget is.
    [Fact]
    public async Task SlopeCellsAtResolution40_JustInsideTheImpliedSubcellBudget_AreAccepted_WithBoundedCost()
    {
        Outcome baseline = await RunAsync(chunks => chunks, expectError: false);
        Outcome outcome = await RunAsync(chunks => Replace(chunks, 0, 0, HugeBlob(chunks, 0, 0, cellCount: SlopeCellsJustInside, shape: GeometryShapeKind.Slope, overrides: false)), expectError: false);

        long blobBytes = outcome.AllocatedBytes - baseline.AllocatedBytes;
        output.WriteLine($"accepted implied worst case: {outcome.AllocatedBytes / (1024d * 1024d):N0} MB allocated ({blobBytes / (1024d * 1024d):N0} MB over the {baseline.AllocatedBytes / (1024d * 1024d):N0} MB baseline), {outcome.Milliseconds:N0} ms, {outcome.RetainedBytes / (1024d * 1024d):N0} MB retained");
        Assert.Null(outcome.Error);
        Assert.True(blobBytes < 300L << 20, $"{blobBytes:N0} bytes allocated for the blob");
    }

    private const int SlopeCellsJustInside = 38;

    // Valid digest, but not what the encoder writes: trailing bytes.
    [Fact]
    public async Task ABlobWithTrailingBytes_EndsTheSessionAsAProtocolError()
    {
        Outcome outcome = await RunAsync(chunks =>
        {
            PersistedTerrainChunk victim = chunks.First(c => c is { ChunkX: 1, ChunkZ: 1 });
            return Replace(chunks, 1, 1, victim with { Data = [.. victim.Data, 0] });
        });

        AssertProtocolError(outcome, "trailing bytes");
    }

    // Valid digest, reserved header field set.
    [Fact]
    public async Task ABlobWithANonZeroReservedField_EndsTheSessionAsAProtocolError()
    {
        Outcome outcome = await RunAsync(chunks =>
        {
            PersistedTerrainChunk victim = chunks.First(c => c is { ChunkX: 2, ChunkZ: 0 });
            byte[] data = [.. victim.Data];
            data[6] = 1;
            return Replace(chunks, 2, 0, victim with { Data = data });
        });

        AssertProtocolError(outcome, "reserved field");
    }

    // Valid digest, the row's cell count disagrees with the blob.
    [Fact]
    public async Task ARowWhoseCellCountDiffersFromItsBlob_EndsTheSessionAsAProtocolError()
    {
        Outcome outcome = await RunAsync(chunks =>
        {
            PersistedTerrainChunk victim = chunks.First(c => c is { ChunkX: 0, ChunkZ: 3 });
            return Replace(chunks, 0, 3, victim with { CellCount = victim.CellCount + 1 });
        });

        AssertProtocolError(outcome, "cell count");
    }

    // Valid digest, random bytes behind a good header-sized blob.
    [Fact]
    public async Task AGarbageBlob_EndsTheSessionAsAProtocolError()
    {
        Outcome outcome = await RunAsync(chunks =>
        {
            PersistedTerrainChunk victim = chunks.First(c => c is { ChunkX: 3, ChunkZ: 3 });
            byte[] data = new byte[4096];
            new Random(5).NextBytes(data);
            return Replace(chunks, 3, 3, victim with { Data = data });
        });

        Assert.NotNull(outcome.Error);
        Assert.StartsWith("Protocol error: stored terrain", outcome.Error, StringComparison.Ordinal);
        Assert.False(outcome.Connected);
        Assert.Equal(outcome.Error, outcome.DisconnectReason);
        Assert.True(outcome.ServerSocketStillOpen);
    }

    private void AssertProtocolError(Outcome outcome, string expectedFragment)
    {
        Assert.True(outcome.TerrainReachedTheApplier, "the transport accepted the terrain");
        Assert.NotNull(outcome.Error);
        Assert.False(outcome.Connected, "the session must be over");
        // The applier decodes eagerly and wraps a failure as "Protocol error: stored terrain <what>".
        Assert.StartsWith("Protocol error: stored terrain", outcome.DisconnectReason, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, outcome.DisconnectReason, StringComparison.Ordinal);
        Assert.Equal(outcome.Error, outcome.DisconnectReason);
        output.WriteLine($"{expectedFragment}: {outcome.AllocatedBytes / (1024d * 1024d):N0} MB allocated, {outcome.Milliseconds:N0} ms; reason: {outcome.DisconnectReason}");
        Assert.True(outcome.ServerSocketStillOpen, "the session must end by the client's decision, not by the server closing");
    }

    private sealed record Outcome(
        bool TerrainReachedTheApplier,
        string? Error,
        bool Connected,
        string? DisconnectReason,
        bool ServerSocketStillOpen,
        long AllocatedBytes,
        long RetainedBytes,
        double Milliseconds);

    private static IReadOnlyList<PersistedTerrainChunk> Replace(
        IReadOnlyList<PersistedTerrainChunk> chunks, int chunkX, int chunkZ, byte[] data)
    {
        PersistedTerrainChunk original = chunks.First(c => c.ChunkX == chunkX && c.ChunkZ == chunkZ);
        return Replace(chunks, chunkX, chunkZ, original with { Data = data, CellCount = ReadCellCount(data) });
    }

    private static IReadOnlyList<PersistedTerrainChunk> Replace(
        IReadOnlyList<PersistedTerrainChunk> chunks, int chunkX, int chunkZ, PersistedTerrainChunk replacement) =>
        [.. chunks.Select(c => c.ChunkX == chunkX && c.ChunkZ == chunkZ ? replacement : c)];

    private static int ReadCellCount(byte[] data) => data[20] | (data[21] << 8);

    // The chunk's real cells plus cellCount sub-divided cubes (resolution 40, every child an override) in
    // free slots above the floor: a few hundred KB that decode to millions of overrides.
    private static byte[] HugeBlob(
        IReadOnlyList<PersistedTerrainChunk> chunks, int chunkX, int chunkZ, int cellCount,
        GeometryShapeKind shape = GeometryShapeKind.SolidCube, bool overrides = true)
    {
        MapDefinition definition = new(56, 8, 56, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);
        PersistedTerrainChunk source = chunks.First(c => c.ChunkX == chunkX && c.ChunkZ == chunkZ);
        List<GeometryCellRecord> cells = TerrainChunkCodec.Decode(definition, chunkX, chunkZ, source.Data);
        HashSet<GeometryCellKey> used = [.. cells.Select(c => c.Key)];
        GeometryCellDefinition cube = new(shape, GeometryMaterialId.Stone, 40, IsSolid: true, IsFloor: false);
        int added = 0;
        for (int y = 1; y < 8 && added < cellCount; y++)
        {
            for (int x = chunkX * 16; x < Math.Min(56, (chunkX * 16) + 16) && added < cellCount; x++)
            {
                for (int z = chunkZ * 16; z < Math.Min(56, (chunkZ * 16) + 16) && added < cellCount; z++)
                {
                    GeometryCellKey key = new(x, y, z);
                    if (used.Contains(key))
                    {
                        continue;
                    }

                    GeometrySubdivisionState state = new(40);
                    if (overrides)
                    {
                        state.SetAllChildrenEmpty();
                    }

                    for (int a = 0; a < 40 && overrides; a++)
                    {
                        for (int b = 0; b < 40; b++)
                        {
                            for (int c = 0; c < 40; c++)
                            {
                                state.SetChildSolid(new GeometrySubcellKey(a, b, c), true);
                            }
                        }
                    }

                    cells.Add(new GeometryCellRecord(key, cube, state));
                    added++;
                }
            }
        }

        Assert.Equal(cellCount, added);
        return TerrainChunkCodec.Encode(definition, chunkX, chunkZ, cells);
    }

    private static async Task<Outcome> RunAsync(Func<IReadOnlyList<PersistedTerrainChunk>, IReadOnlyList<PersistedTerrainChunk>> hostile, bool expectError = true)
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);
        JoinAcceptedMessage join = host.JoinSession("hostile-session", "Hostile");
        WorldDescriptor descriptor = join.World!.ToDescriptor();
        TerrainTransferPayload payload = TerrainTransferPayload.Create(descriptor, hostile(fixture.Chunks), fragmentRawBytes: 64 * 1024);

        const int transferId = 1;
        List<IServerToClientMessage> script =
        [
            new ServerHelloMessage("hostile-server", TransportEnvelope.CurrentProtocolVersion, 0),
            join with { InitialWorld = null, WorldTransferId = transferId, Terrain = payload.Manifest, TerrainPayload = null },
            .. TerrainTransferTestData.Fragments(payload, transferId),
            new WorldChunkMessage(transferId, 0, true, join.InitialWorld!.Value)
        ];

        await using HostileServer server = HostileServer.Start(script);
        await using RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.Port, "hostile-session", "Hostile", "hostile-test"))
            .AsTask().WaitAsync(Timeout);

        JoinAcceptedMessage? received = null;
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (received == null && DateTime.UtcNow < deadline && session.IsConnected)
        {
            if (session.TryDequeueInbound(out IServerToClientMessage? message) && message is JoinAcceptedMessage { ReceivedTerrain: not null } accepted)
            {
                received = accepted;
            }
            else
            {
                await Task.Delay(5);
            }
        }

        if (received == null)
        {
            return new Outcome(false, null, session.IsConnected, session.DisconnectReason, true, 0, 0, 0);
        }

        using ClientWorldSession world = new(20, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string? error = null;
        try
        {
            // PlayScene.ApplyReplicatedMessage: the applier throws, the scene ends the session.
            ClientJoinAcceptedApplier.ApplyToRuntime(world, received);
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            session.EndSessionWithReason(ex.Message, RemoteClientSession.ProtocolErrorLogEvent);
        }

        double milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long retained = GC.GetTotalMemory(forceFullCollection: true);
        Assert.True(expectError == (error != null), $"expectError={expectError}, error={error}");
        return new Outcome(true, error, session.IsConnected, session.DisconnectReason, server.IsOpen, allocated, retained, milliseconds);
    }

    // Accepts one client, waits for its hello and join request, writes the script, then keeps the socket
    // open (reading and discarding) until disposed.
    private sealed class HostileServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serve;
        private volatile bool _open = true;

        private HostileServer(TcpListener listener, IReadOnlyList<IServerToClientMessage> script)
        {
            _listener = listener;
            _serve = Task.Run(() => ServeAsync(script, _shutdown.Token));
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public bool IsOpen => _open;

        public static HostileServer Start(IReadOnlyList<IServerToClientMessage> script)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return new HostileServer(listener, script);
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
            {
            }

            _shutdown.Dispose();
        }

        private async Task ServeAsync(IReadOnlyList<IServerToClientMessage> script, CancellationToken cancellationToken)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
            NetworkStream stream = client.GetStream();
            LengthPrefixedMessageFramer framer = new();
            TransportJsonCodec codec = new();
            List<byte> pending = [];
            byte[] chunk = new byte[4096];
            int clientMessages = 0;
            bool sent = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    _open = false;
                    return;
                }

                pending.AddRange(chunk.AsSpan(0, read).ToArray());
                while (framer.TryDeframe(pending.ToArray(), out byte[]? payload, out int consumed))
                {
                    pending.RemoveRange(0, consumed);
                    if (payload!.Length > 0)
                    {
                        clientMessages++;
                    }
                }

                if (!sent && clientMessages >= 2)
                {
                    sent = true;
                    ulong sequence = 1;
                    foreach (IServerToClientMessage message in script)
                    {
                        // Encode<T> serialises T's declared type; pass the runtime type, not the interface.
                        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence++);
                        byte[] encoded = message switch
                        {
                            ServerHelloMessage hello => codec.Encode(envelope, hello),
                            JoinAcceptedMessage joinAccepted => codec.Encode(envelope, joinAccepted),
                            TerrainChunkMessage terrain => codec.Encode(envelope, terrain),
                            WorldChunkMessage world => codec.Encode(envelope, world),
                            _ => throw new InvalidOperationException(message.MessageKind)
                        };
                        await stream.WriteAsync(framer.Frame(encoded), cancellationToken);
                    }
                }
            }
        }
    }
}
