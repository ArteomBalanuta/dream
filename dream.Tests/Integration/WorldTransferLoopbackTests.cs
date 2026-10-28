using System.Net;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Load;
using Dream.Tests.Multiplayer.Protocol;
using Dream.Tests.Utilities;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Integration;

// SCALE-1 end to end: join and resync worlds sent as protocol v3 chunked transfers by the real
// ServerConnection and reassembled by the real RemoteClientSession.
public class WorldTransferLoopbackTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // A seeded 10k-cube world (ids, rubble with pieces) joins over loopback TCP with the old
    // symmetric 1 MB frame cap on both ends, which no protocol v2 client could join through
    // (its single JoinAccepted frame was about 1.75 MB), and the client's world ends on the
    // server's checksum.
    [Fact]
    public async Task World10k_JoinsOverLoopback_InChunksUnderTheOldFrameCap_AndMatchesTheServer()
    {
        string root = LoadPaths.FindRepoRoot();
        string work = Path.Combine(Path.GetTempPath(), $"dream-scale1-loopback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            string databasePath = Path.Combine(work, "world.sqlite");
            WorldSeedResult seed = WorldSeeder.Seed(databasePath, Path.Combine(root, "dream", "config", "player-profiles.init.sql"), LoadScenario.World10k);
            ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(databasePath, Path.Combine(root, "dream", "config", "player-profiles.init.sql"));
            WorldSessionRuntime runtime = new(
                floorSize: LoadScenario.World10k.FloorSize,
                playerStartPosition: new Vector3(5f, 1.5f, 5f),
                new SceneEventHub(),
                playerProfiles: bundle.PlayerProfiles,
                worldPersistence: bundle.World,
                combatFormulaService: bundle.FormulaService,
                persistenceWrites: PersistenceWriteQueue.Background(),
                simulationSeed: bundle.SimulationSeed ?? WorldSessionRuntime.DefaultSimulationSeed)
            {
                WriteCheckpointOnDispose = false
            };
            ProtocolRouter router = new(
                [new ClientHelloHandler(), new JoinRequestHandler(), new PlayerCommandHandler(), new PlayerActionHandler(), new WorldResyncRequestHandler()],
                Array.Empty<IClientMessageHandler>());
            using DedicatedServerHost host = new(new RuntimeAuthoritativeSessionHost(runtime, bundle), router, deferJoinsToTick: true);
            await using DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                PerTickSnapshots.Options);
            service.Start();

            Task<ServerConnection> accept = service.AcceptOnThreadPoolAsync();
            await using RemoteClientSession client = await RemoteClientSession.ConnectAsync(
                new RemoteClientOptions("127.0.0.1", service.Port, "w10k", "World Ten K", "scale1-test"),
                framer: new LengthPrefixedMessageFramer());
            ServerConnection connection = await accept.WaitAsync(Timeout);
            JoinAcceptedMessage accepted = await TickUntilJoinedAsync(service, client);

            Assert.Null(accepted.WorldTransferId);
            WorldSnapshot world = Assert.IsType<WorldSnapshot>(accepted.InitialWorld);
            Assert.True(world.PlacedCubes!.Count >= seed.PlacedCubes, $"{world.PlacedCubes.Count} cubes for {seed.PlacedCubes} seeded");
            Assert.Equal(seed.RubbleCells, world.FracturedCubes!.Count);
            Assert.True(world.PlacedCubes.Count(cube => cube.ActorId.HasValue) >= seed.PlacedCubes);
            Assert.Equal(1, connection.WorldTransfers);
            Assert.Equal(1, client.WorldTransfersCompleted);
            Assert.True(client.IsJoined);
            output.WriteLine(
                $"{world.PlacedCubes.Count} cubes, {world.FracturedCubes.Count} rubble cells in {connection.WorldChunksSent} chunk(s); " +
                $"{connection.BytesSent:N0} B sent to the client in total");

            ulong serverChecksum = WorldStateChecksum.Compute(runtime.Map);
            using ClientWorldSession clientWorld = new(world.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            ClientJoinAcceptedApplier.ApplyToRuntime(clientWorld, accepted);
            Assert.Equal(serverChecksum, WorldStateChecksum.Compute(clientWorld.Map));
            Assert.Equal(runtime.LastWorldMutationSequence, clientWorld.LastAppliedReplicatedWorldMutationSequence);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // A 50k-cube world (plus 1,000 rubble cells) through a real ServerConnection to a real client
    // session over loopback, with the 1 MB cap on both ends: about 11x what a protocol v2 frame
    // could hold under that cap. Every chunk fits, regular snapshots queued behind the join go out
    // between the chunks, and the client gets the whole world, then those snapshots.
    [Fact]
    public async Task World50k_JoinsThroughChunks_ThatEachFitTheOldFrameCap()
    {
        WorldSnapshot world = WorldTransferProtocolTests.CreateWorld(cubes: 50_000, rubble: 1_000, subcells: 10);
        int v2Bytes = JsonSerializer.SerializeToUtf8Bytes(world, ProtocolWireOptions.V2).Length;
        Assert.True(v2Bytes > LengthPrefixedMessageFramer.DefaultMaxPayloadBytes * 5, $"v2 world is only {v2Bytes} B");

        await using TcpServerTransport transport = new(IPAddress.Loopback, 0);
        transport.Start();
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        Task<ITransportConnection> accept = transport.AcceptClientAsync().AsTask();
        Task<RemoteClientSession> connect = RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", transport.Port, "w50k", "World Fifty K", "scale1-test"),
            framer: new LengthPrefixedMessageFramer()).AsTask();
        await using ServerConnection connection = new(await accept.WaitAsync(Timeout), host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);
        connection.Enqueue(new ServerHelloMessage("scale1-test", TransportEnvelope.CurrentProtocolVersion, 0));
        await using RemoteClientSession client = await connect.WaitAsync(Timeout);

        connection.Enqueue(new JoinAcceptedMessage("w50k", 1, 40, world, InitialWorldMutationSequence: world.MutationSequence));
        for (ulong tick = 41; tick <= 45; tick++)
        {
            connection.EnqueueSnapshot(new ServerSnapshotMessage(CreateSnapshot(tick, new Dictionary<string, ulong>(), [])));
        }

        Assert.True(await connection.FlushOutboundAsync(Timeout));
        List<IServerToClientMessage> received = await DrainUntilAsync(client, messages => messages.OfType<ServerSnapshotMessage>().Any(snapshot => snapshot.Snapshot.ServerTick == 45));

        Assert.True(client.IsConnected, client.DisconnectReason);
        Assert.IsType<ServerHelloMessage>(received[0]);
        JoinAcceptedMessage accepted = Assert.IsType<JoinAcceptedMessage>(received[1]);
        Assert.Equal(world.PlacedCubes!.Count, accepted.InitialWorld!.Value.PlacedCubes!.Count);
        Assert.Equal(world.FracturedCubes!.Count, accepted.InitialWorld.Value.FracturedCubes!.Count);
        Assert.Equal(world.PlacedCubes.Select(cube => (cube.CellPosition, cube.MaterialId, cube.ActorId)), accepted.InitialWorld.Value.PlacedCubes.Select(cube => (cube.CellPosition, cube.MaterialId, cube.ActorId)));
        Assert.True(received.Skip(2).All(message => message is ServerSnapshotMessage));
        long average = connection.BytesSent / Math.Max(1, connection.FramesSent);
        output.WriteLine($"v2 single frame {v2Bytes:N0} B; v3 {connection.WorldChunksSent} chunks, {connection.BytesSent:N0} B in {connection.FramesSent} frames");
        Assert.True(connection.WorldChunksSent >= 2);
        Assert.True(connection.BytesSent * 4 < v2Bytes, $"v3 sent {connection.BytesSent} B against v2's {v2Bytes} B");
    }

    // A world mutation that the server produces while a join transfer is still going out lands
    // between two chunks on the wire. The client holds it until the world is complete, then
    // applies it on top: a delta already in the world (by sequence) stays skipped, later ones apply.
    [Fact]
    public async Task MutationsArrivingMidTransfer_ApplyOnTopOfTheTransferredWorld()
    {
        WorldSnapshot world = WorldTransferProtocolTests.CreateWorld(cubes: 3_000, rubble: 20, subcells: 0);
        ulong worldSequence = world.MutationSequence!.Value;
        PlacedCubeSnapshot removed = world.PlacedCubes![12];
        Assert.NotNull(removed.ActorId);
        Vector3 alreadyReflected = new(40f, 5f, 40f);
        Vector3 placedLater = new(41f, 5f, 41f);

        GatedTransport transport = new("mid-transfer");
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        await using ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);
        connection.WorldChunkBytes = 2048;
        transport.Close();
        connection.Enqueue(new JoinAcceptedMessage("session-a", 1, 9, world, InitialWorldMutationSequence: worldSequence));
        // The writer is now blocked sending the carrier; the tick's snapshots queue behind it.
        Assert.True(await transport.SendStarted.WaitAsync(Deadline));
        connection.EnqueueSnapshot(new ServerSnapshotMessage(CreateSnapshot(10, new Dictionary<string, ulong>(),
        [
            // Already in the world (sequence <= the world's): a replay would add a stray cube.
            new WorldMutationDelta(WorldMutationKind.CubePlaced, alreadyReflected, MaterialId: GeometryMaterialId.Dirt, Sequence: worldSequence - 1),
            new WorldMutationDelta(WorldMutationKind.CubePlaced, placedLater, MaterialId: GeometryMaterialId.Stone, Sequence: worldSequence + 1, ActorId: 555)
        ])));
        connection.EnqueueSnapshot(new ServerSnapshotMessage(CreateSnapshot(11, new Dictionary<string, ulong>(),
        [
            new WorldMutationDelta(WorldMutationKind.CubeDestroyed, removed.CellPosition, Sequence: worldSequence + 2, ActorId: removed.ActorId)
        ])));
        transport.Open();
        Assert.True(await connection.FlushOutboundAsync(Deadline));

        IReadOnlyList<IProtocolMessage> wire = Decode(transport.Frames);
        Assert.IsType<JoinAcceptedMessage>(wire[0]);
        Assert.Equal(1, ((JoinAcceptedMessage)wire[0]).WorldTransferId);
        int snapshotIndex = wire.ToList().FindIndex(message => message is ServerSnapshotMessage);
        int lastChunk = wire.ToList().FindLastIndex(message => message is WorldChunkMessage);
        Assert.True(snapshotIndex > 1 && snapshotIndex < lastChunk, $"the snapshot went out at {snapshotIndex}, the last chunk at {lastChunk}");
        output.WriteLine($"{wire.Count(message => message is WorldChunkMessage)} chunks; the snapshot went out after chunk {snapshotIndex - 2}");

        // The client session, fed the frames one by one: nothing comes out before the last chunk.
        ScriptedTransport scripted = new(transport.Frames);
        await using RemoteClientSession client = new(scripted, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), new RemoteClientOptions("127.0.0.1", 1, "session-a", "A", "scale1-test"));
        for (int i = 0; i < lastChunk; i++)
        {
            Assert.True(await client.ProcessInboundOnceAsync());
            Assert.False(client.TryDequeueInbound(out _), $"a message came out before the world was complete (frame {i})");
            Assert.True(client.IsReceivingWorld);
            Assert.False(client.IsJoined);
        }

        Assert.True(await client.ProcessInboundOnceAsync());
        Assert.False(client.IsReceivingWorld);
        Assert.True(client.IsJoined);
        List<IServerToClientMessage> delivered = [];
        while (client.TryDequeueInbound(out IServerToClientMessage? message))
        {
            delivered.Add(message!);
        }

        JoinAcceptedMessage accepted = Assert.IsType<JoinAcceptedMessage>(delivered[0]);
        ServerSnapshotMessage snapshot = Assert.IsType<ServerSnapshotMessage>(Assert.Single(delivered.Skip(1)));
        Assert.Equal(3, snapshot.Snapshot.WorldMutations.Count);

        using ClientWorldSession clientWorld = new(world.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        ClientJoinAcceptedApplier.ApplyToRuntime(clientWorld, accepted);
        Assert.NotNull(clientWorld.Map.TryGetCubeAtCell((int)removed.CellPosition.X, (int)removed.CellPosition.Y, (int)removed.CellPosition.Z));
        RemoteWorldMutationApplier.ApplySnapshotToRuntime(clientWorld, snapshot.Snapshot);

        Assert.Null(clientWorld.Map.TryGetCubeAtCell((int)alreadyReflected.X, (int)alreadyReflected.Y, (int)alreadyReflected.Z));
        Cube? later = clientWorld.Map.TryGetCubeAtCell((int)placedLater.X, (int)placedLater.Y, (int)placedLater.Z);
        Assert.NotNull(later);
        Assert.True(clientWorld.Map.TryGetReplicatedId(later!, out int laterId));
        Assert.Equal(555, laterId);
        Assert.Equal(GeometryMaterialId.Stone, clientWorld.Map.TryGetGeometryCellAtCell((int)placedLater.X, (int)placedLater.Y, (int)placedLater.Z)!.Definition.MaterialId);
        Assert.Null(clientWorld.Map.TryGetCubeAtCell((int)removed.CellPosition.X, (int)removed.CellPosition.Y, (int)removed.CellPosition.Z));
        Assert.Equal(worldSequence + 2, clientWorld.LastAppliedReplicatedWorldMutationSequence);
        Assert.Equal(world.PlacedCubes.Count, clientWorld.Map.SolidCubes.Count(cube => cube.Position.Y > 1f));
    }

    // A checksum-mismatch resync takes the same chunked path as a join: the server sends a carrier
    // snapshot and chunks, the client session delivers one snapshot with the whole world, and
    // the client's diverged world heals to the server's checksum.
    [Fact]
    public async Task Resync_IsSentInChunks_AndHealsTheClient()
    {
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(DedicatedServerHost.CreateRuntimeAttached(persistence: null, deferJoinsToTick: true));
        Task<ServerConnection> accept = server.Service.AcceptOnThreadPoolAsync();
        await using RemoteClientSession client = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.Port, "resync-a", "Resync A", "scale1-test"));
        ServerConnection connection = await accept.WaitAsync(Timeout);
        JoinAcceptedMessage accepted = await TickUntilJoinedAsync(server.Service, client);
        using ClientWorldSession clientWorld = new(accepted.InitialWorld!.Value.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        ClientJoinAcceptedApplier.ApplyToRuntime(clientWorld, accepted);
        ulong serverChecksum = WorldStateChecksum.Compute(server.Runtime.Map);
        Assert.Equal(serverChecksum, WorldStateChecksum.Compute(clientWorld.Map));
        long chunksAfterJoin = connection.WorldChunksSent;

        // Diverge: a stray local cube, as a lost delta would leave.
        (int strayX, int strayZ) = Enumerable.Range(2, clientWorld.Map.Width - 4)
            .SelectMany(x => Enumerable.Range(2, clientWorld.Map.Depth - 4).Select(z => (x, z)))
            .First(cell => clientWorld.Map.TryGetGeometryCellAtCell(cell.x, 5, cell.z) == null && server.Runtime.Map.TryGetGeometryCellAtCell(cell.x, 5, cell.z) == null);
        Assert.NotNull(clientWorld.Map.TryAddCube(new Vector3(strayX, 5f, strayZ), withPhysics: false));
        Assert.NotEqual(serverChecksum, WorldStateChecksum.Compute(clientWorld.Map));
        connection.WorldChunkBytes = 1024;
        client.QueueWorldResyncRequest(WorldStateChecksum.Compute(clientWorld.Map));

        ServerSnapshotMessage? resync = null;
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (resync == null)
        {
            Assert.True(DateTime.UtcNow < deadline, "no resync arrived");
            await server.Service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            Assert.True(await server.Service.FlushOutboundAsync(Timeout));
            await Task.Delay(5);
            while (client.TryDequeueInbound(out IServerToClientMessage? message))
            {
                if (message is ServerSnapshotMessage snapshot)
                {
                    Assert.Null(snapshot.WorldTransferId);
                    RemoteWorldMutationApplier.ApplySnapshotToRuntime(clientWorld, snapshot.Snapshot);
                    if (snapshot.Snapshot.World != null)
                    {
                        resync = snapshot;
                    }
                }
            }
        }

        Assert.Equal(2, connection.WorldTransfers);
        Assert.Equal(2, client.WorldTransfersCompleted);
        Assert.True(connection.WorldChunksSent - chunksAfterJoin >= 3, $"the resync took {connection.WorldChunksSent - chunksAfterJoin} chunk(s)");
        Assert.Null(clientWorld.Map.TryGetCubeAtCell(strayX, 5, strayZ));
        Assert.Equal(WorldStateChecksum.Compute(server.Runtime.Map), WorldStateChecksum.Compute(clientWorld.Map));
    }

    // The tick's pooled capture goes back to the pool once its last chunk is written, and also
    // when the connection ends with the transfer unfinished.
    [Fact]
    public async Task TheCapture_IsReleased_AfterTheLastChunk_OrWhenTheConnectionEndsMidTransfer()
    {
        using RuntimeAuthoritativeSessionHost server = RuntimeAuthoritativeSessionHost.CreateDefault();
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();

        WorldSnapshotCapture sent = server.Runtime.CaptureWorldSnapshot();
        GatedTransport complete = new("complete");
        await using (ServerConnection connection = new(complete, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness))
        {
            connection.WorldChunkBytes = 512;
            connection.Enqueue(new JoinAcceptedMessage("session-a", 1, 1, sent.ToWorldSnapshot()));
            Assert.True(await connection.FlushOutboundAsync(Deadline));
            Assert.True(connection.WorldChunksSent >= 3);
        }

        Assert.True(sent.IsReleased);

        WorldSnapshotCapture abandoned = server.Runtime.CaptureWorldSnapshot();
        GatedTransport stalled = new("stalled");
        ServerConnection stalledConnection = new(stalled, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);
        stalledConnection.WorldChunkBytes = 512;
        stalled.Close();
        stalledConnection.Enqueue(new JoinAcceptedMessage("session-a", 1, 1, abandoned.ToWorldSnapshot()));
        Assert.True(await stalled.SendStarted.WaitAsync(Deadline));
        Assert.False(abandoned.IsReleased);
        // Closing the socket aborts the stalled write, as the service does at shutdown.
        await stalledConnection.CloseTransportAsync();
        await stalledConnection.DisposeAsync();
        Assert.True(abandoned.IsReleased);
    }

    private static async Task<JoinAcceptedMessage> TickUntilJoinedAsync(DedicatedServerService service, RemoteClientSession client)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Assert.True(DateTime.UtcNow < deadline, "the join never completed");
            Assert.True(client.IsConnected, client.DisconnectReason);
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            await Task.Delay(10);
            while (client.TryDequeueInbound(out IServerToClientMessage? message))
            {
                if (message is JoinAcceptedMessage accepted)
                {
                    return accepted;
                }
            }
        }
    }

    private static async Task<List<IServerToClientMessage>> DrainUntilAsync(RemoteClientSession client, Func<List<IServerToClientMessage>, bool> done)
    {
        List<IServerToClientMessage> received = [];
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!done(received))
        {
            Assert.True(DateTime.UtcNow < deadline, $"only {received.Count} messages arrived");
            Assert.True(client.IsConnected, client.DisconnectReason);
            while (client.TryDequeueInbound(out IServerToClientMessage? message))
            {
                received.Add(message!);
            }

            await Task.Delay(5);
        }

        return received;
    }

    // Hands out one recorded frame per receive, then reports the connection closed.
    private sealed class ScriptedTransport(IReadOnlyList<byte[]> frames) : ITransportConnection
    {
        private int _next;

        public string ConnectionId => "scripted";

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next >= frames.Count)
            {
                return ValueTask.FromResult(0);
            }

            byte[] frame = frames[_next++];
            Assert.True(frame.Length <= buffer.Length, "a frame is larger than the client's receive chunk");
            frame.CopyTo(buffer);
            return ValueTask.FromResult(frame.Length);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
