using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Determinism;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Server;

// Wave 10 fix loop 2 (reviewer S2): one advance runs up to 8 ticks and nothing caps one tick's
// fractures or damage, so a broadcast snapshot can carry more deltas than the merge caps allow
// and its shared payload can pass the server-to-client frame cap. Before the fix every
// connection's writer threw on that payload in the same tick and every player dropped. Now the
// broadcast goes out without its deltas and every joined connection gets a world resync on the
// coalesce-overflow path; a payload that passes the frame cap under the caps is caught the same
// way at the writer.
public sealed class OversizedBroadcastSnapshotTests(ITestOutputHelper output)
{
    private const int TerrainFloorSize = 32;
    private const int ConnectionCount = 3;
    private const int MutationCap = 8;
    private const int FracturedCubes = 20;

    // Real fractures on a real runtime: one advance carries 20 fracture mutations against a cap
    // of 8. No connection faults, every client gets a world resync, and every client's world ends
    // on the server's checksum. Without the resync the dropped fractures would leave the clients
    // 20 cells behind, so the checksum comparison would fail.
    [Fact]
    public async Task BroadcastWithMoreFracturesThanTheCap_DropsTheDeltas_ResyncsEveryClient_AndTheChecksumsConverge()
    {
        SceneEventHub events = new();
        WorldSessionRuntime runtime = new(TerrainFloorSize, new Vector3(5f, 1.5f, 5f), events);
        DedicatedServerHost host = CreateHost(new RuntimeAuthoritativeSessionHost(runtime));
        ServerConnectionOptions options = WithoutLiveness with { MaxCoalescedWorldMutations = MutationCap, SnapshotIntervalTicks = 1 };
        DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport[] transports = new GatedTransport[ConnectionCount];
        ServerConnection[] connections = new ServerConnection[ConnectionCount];
        for (int i = 0; i < ConnectionCount; i++)
        {
            transports[i] = new GatedTransport($"connection-{i}");
            connections[i] = new ServerConnection(transports[i], host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options)
            {
                WorldChunkBytes = 16 * 1024
            };
            service.AttachConnection(connections[i]);
        }

        try
        {
            for (int i = 0; i < ConnectionCount; i++)
            {
                // What the connection's receive loop does with a client's join: its replies (the
                // JoinAccepted with the join world) are queued on the connection.
                foreach (IServerToClientMessage reply in host.HandleClientMessage($"connection-{i}", new ClientJoinRequestMessage($"session-{i}", $"Player {i}")))
                {
                    connections[i].Enqueue(reply);
                }

                Assert.True(host.IsConnectionJoined($"connection-{i}"));
            }

            for (int tick = 0; tick < 3; tick++)
            {
                await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            }

            Assert.True(await service.FlushOutboundAsync(Deadline));
            Assert.All(connections, connection => Assert.Equal(1, connection.WorldTransfers));

            // One tick's worth of fractures, published the way the runtime's combat path does.
            List<GeometryCellKey> fractured = FractureCubes(runtime, events, FracturedCubes);
            ulong fractureTick = runtime.CurrentServerTick + 1;
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            Assert.Equal(1, service.OversizedBroadcasts);
            Assert.All(connections, connection => Assert.Equal(1, connection.OverflowResyncs));

            // The resync is served after the next tick; a few more ticks let it and the snapshots
            // behind it go out.
            for (int tick = 0; tick < 6; tick++)
            {
                await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
                Assert.True(await service.FlushOutboundAsync(Deadline));
            }

            Assert.True(await service.FlushOutboundAsync(Deadline));
            Assert.Equal(ConnectionCount, service.ConnectionCount);
            ulong serverChecksum = WorldStateChecksum.Compute(runtime.Map);
            for (int i = 0; i < ConnectionCount; i++)
            {
                ServerConnection connection = connections[i];
                Assert.False(connection.IsFaulted, connection.CloseReason.ToString());
                Assert.Equal(2, connection.WorldTransfers);
                Assert.Equal(0, connection.OversizedSnapshotFrames);

                IReadOnlyList<IProtocolMessage> wire = Decode(transports[i].Frames);
                List<IServerToClientMessage> delivered = WorldTransferReassembler.Reassemble(wire);
                ServerSnapshotMessage stripped = Assert.Single(
                    delivered.OfType<ServerSnapshotMessage>(),
                    message => message.Snapshot.World == null && message.Snapshot.ServerTick == fractureTick);
                Assert.Empty(stripped.Snapshot.WorldMutations);
                Assert.Empty(stripped.Snapshot.DamageEvents);
                Assert.Null(stripped.Snapshot.WorldChecksum);
                Assert.NotEmpty(stripped.Snapshot.Actors);
                Assert.DoesNotContain(
                    delivered.OfType<ServerSnapshotMessage>(),
                    message => message.Snapshot.WorldMutations.Any(mutation => mutation.Kind == WorldMutationKind.CubeFractured));
                ServerSnapshotMessage resync = Assert.Single(
                    delivered.OfType<ServerSnapshotMessage>(),
                    message => message.Snapshot.World != null);
                Assert.True(resync.Snapshot.ServerTick > fractureTick);

                JoinAcceptedMessage accepted = Assert.IsType<JoinAcceptedMessage>(delivered[0]);
                WorldSnapshot joinWorld = Assert.IsType<WorldSnapshot>(accepted.InitialWorld);
                using ClientWorldSession client = new(joinWorld.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
                ClientJoinAcceptedApplier.ApplyToRuntime(client, accepted);
                ulong? beforeResync = null;
                foreach (ServerSnapshotMessage snapshot in delivered.OfType<ServerSnapshotMessage>())
                {
                    if (snapshot.Snapshot.World != null)
                    {
                        beforeResync = WorldStateChecksum.Compute(client.Map);
                    }

                    RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, snapshot.Snapshot);
                }

                // The dropped deltas left the client behind until the resync, which caught it up.
                Assert.NotEqual(serverChecksum, beforeResync);
                Assert.Equal(serverChecksum, WorldStateChecksum.Compute(client.Map));
                foreach (GeometryCellKey cell in fractured)
                {
                    Assert.NotNull(client.Map.TryGetRubbleAtCell(cell));
                }

                output.WriteLine(
                    $"connection-{i}: {wire.Count} frames, {connection.WorldTransfers} transfers, {connection.OverflowResyncs} overflow resyncs, {connection.BytesSent:N0} B");
            }

            ServerMetricsSnapshot metrics = service.CaptureMetrics();
            Assert.Equal(ConnectionCount, metrics.CoalesceOverflowResyncs);
        }
        finally
        {
            await service.DisposeAsync();
            host.Dispose();
        }
    }

    // A broadcast under the count caps whose frame still passes the server-to-client cap: each
    // connection's writer sends it without its deltas and flags a resync instead of faulting.
    [Fact]
    public async Task BroadcastPayloadPastTheFrameCap_IsSentWithoutDeltas_AndNoConnectionFaults()
    {
        MutatingAuthority authority = new() { MutationsPerTick = 100 };
        DedicatedServerHost host = CreateHost(authority);
        ServerConnectionOptions options = WithoutLiveness with { SnapshotIntervalTicks = 1 };
        // 100 mutations are about 6 KB of JSON; the stripped snapshot, the join and the resync
        // world fit in 2 KB.
        LengthPrefixedMessageFramer serverFramer = new(maxReadPayloadBytes: 64 * 1024, maxWritePayloadBytes: 2048);
        DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), serverFramer, options);
        GatedTransport[] transports = new GatedTransport[ConnectionCount];
        ServerConnection[] connections = new ServerConnection[ConnectionCount];
        for (int i = 0; i < ConnectionCount; i++)
        {
            transports[i] = new GatedTransport($"capped-{i}");
            connections[i] = new ServerConnection(transports[i], host, new TransportJsonCodec(), serverFramer, options) { WorldChunkBytes = 1024 };
            service.AttachConnection(connections[i]);
        }

        try
        {
            for (int i = 0; i < ConnectionCount; i++)
            {
                host.HandleClientMessage($"capped-{i}", new ClientJoinRequestMessage($"session-{i}", $"Player {i}"));
            }

            for (int tick = 0; tick < 6; tick++)
            {
                await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
                Assert.True(await service.FlushOutboundAsync(Deadline));
            }

            Assert.Equal(0, service.OversizedBroadcasts);
            Assert.Equal(ConnectionCount, service.ConnectionCount);
            Assert.True(authority.WorldResyncs >= ConnectionCount, $"{authority.WorldResyncs} resyncs served");
            for (int i = 0; i < ConnectionCount; i++)
            {
                ServerConnection connection = connections[i];
                Assert.False(connection.IsFaulted, connection.CloseReason.ToString());
                Assert.True(connection.OversizedSnapshotFrames >= 5, $"{connection.OversizedSnapshotFrames} oversized frames");
                Assert.True(connection.WorldTransfers >= 1, "no resync reached the connection");
                IReadOnlyList<IProtocolMessage> wire = Decode(transports[i].Frames);
                Assert.All(transports[i].Frames, frame => Assert.True(frame.Length - 4 <= 2048, $"a {frame.Length} B frame"));
                ServerSnapshotMessage[] snapshots = [.. wire.OfType<ServerSnapshotMessage>().Where(message => message.WorldTransferId == null)];
                Assert.True(snapshots.Length >= 5);
                Assert.All(snapshots, snapshot => Assert.Empty(snapshot.Snapshot.WorldMutations));
                Assert.Contains(wire, message => message is ServerSnapshotMessage { WorldTransferId: not null });
            }
        }
        finally
        {
            await service.DisposeAsync();
            host.Dispose();
        }
    }

    // The normal path: a broadcast under the caps is sent unchanged, and the cap check on the tick
    // thread allocates nothing.
    [Fact]
    public async Task BroadcastUnderTheCaps_IsSentWithItsDeltas_AndTheCapCheckAllocatesNothing()
    {
        ServerSnapshotEnvelope underCaps = CreateSnapshot(
            7,
            new Dictionary<string, ulong> { ["session-a"] = 7 },
            [.. Enumerable.Range(1, MutationCap).Select(i => Mutation((ulong)i))]);
        ServerSnapshotEnvelope overCaps = underCaps with { WorldMutations = [.. Enumerable.Range(1, MutationCap + 1).Select(i => Mutation((ulong)i))] };
        ServerConnectionOptions options = WithoutLiveness with { MaxCoalescedWorldMutations = MutationCap };
        Assert.False(DedicatedServerService.ExceedsDeltaCaps(underCaps, options));
        Assert.True(DedicatedServerService.ExceedsDeltaCaps(overCaps, options));
        Assert.True(ServerConnection.HasDeltas(underCaps));
        AllocationAssert.AtMost(0, () => DedicatedServerService.ExceedsDeltaCaps(underCaps, options));
        AllocationAssert.AtMost(0, () => ServerConnection.HasDeltas(underCaps));

        MutatingAuthority authority = new() { MutationsPerTick = MutationCap };
        DedicatedServerHost host = CreateHost(authority);
        DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options with { SnapshotIntervalTicks = 1 });
        GatedTransport transport = new("under-caps");
        ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        service.AttachConnection(connection);
        try
        {
            host.HandleClientMessage("under-caps", new ClientJoinRequestMessage("session-a", "Player A"));
            for (int tick = 0; tick < 4; tick++)
            {
                await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
                Assert.True(await service.FlushOutboundAsync(Deadline));
            }

            Assert.Equal(0, service.OversizedBroadcasts);
            Assert.Equal(0, connection.OverflowResyncs);
            Assert.Equal(0, connection.OversizedSnapshotFrames);
            Assert.Equal(0, authority.WorldResyncs);
            ServerSnapshotMessage[] snapshots = [.. Decode(transport.Frames).OfType<ServerSnapshotMessage>()];
            Assert.Equal(4, snapshots.Length);
            Assert.All(snapshots, snapshot => Assert.Equal(MutationCap, snapshot.Snapshot.WorldMutations.Count));
        }
        finally
        {
            await service.DisposeAsync();
            host.Dispose();
        }
    }

    // Fractures count cubes above the floor (the world checksum skips y = 0) and publishes each
    // fracture as the runtime's combat path does, so the runtime queues its mutation.
    private static List<GeometryCellKey> FractureCubes(WorldSessionRuntime runtime, SceneEventHub events, int count)
    {
        Map map = runtime.Map;
        List<(Cube Cube, GeometryCellKey Cell)> candidates = [];
        foreach (Cube cube in map.SolidCubes.OrderBy(cube => cube.Position.X).ThenBy(cube => cube.Position.Z).ThenBy(cube => cube.Position.Y))
        {
            if (map.TryGetCellForCube(cube, out GeometryCellKey cell) && cell.Y >= 1)
            {
                candidates.Add((cube, cell));
            }

            if (candidates.Count == count)
            {
                break;
            }
        }

        Assert.Equal(count, candidates.Count);
        List<GeometryCellKey> fractured = [];
        foreach ((Cube cube, GeometryCellKey cell) in candidates)
        {
            Assert.True(map.FractureCubeIntoPieces(cell), $"fracture of {cell} failed");
            GeometryCellRecord rubble = Assert.IsType<GeometryCellRecord>(map.TryGetRubbleAtCell(cell));
            events.Publish(new CubeFracturedEvent(
                cube,
                new Vector3(cell.X, cell.Y, cell.Z),
                rubble.Definition.MaterialId,
                [.. rubble.FracturePieces!.Where(GeometryFracturePiece.IsValid)]));
            fractured.Add(cell);
        }

        return fractured;
    }
}
