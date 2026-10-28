using System.Reflection;
using System.Threading.Channels;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Multiplayer.Protocol;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Integration;

// Wave 10 tester: the merged features against each other.
//  - SCALE-1 x NETR-8: a takeover while the old connection's world transfer is unfinished.
//  - SCALE-1 x ID-1: a CubeIdentified produced while the join world is still in flight.
//  - SCALE-1 x NETR-1: overflow resyncs requested while a transfer is in flight.
[Collection(TimingSensitiveCollection.Name)]
public sealed class Wave10TransferInteractionTests(ITestOutputHelper output)
{
    private static readonly FieldInfo ActiveTransferField =
        typeof(ServerConnection).GetField("_activeTransfer", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ServerConnection._activeTransfer not found");

    // The old client crashes while its join world is going out (its socket write is stuck after the
    // carrier and one chunk). A rejoin of the same session past the 2 s window takes it over. The new
    // connection gets a transfer of its own (its own capture, its own id sequence, complete and in
    // order, nothing of the old one), the old transfer's pooled capture goes back to the pool when
    // the service closes the old connection, and the new client's world matches the server.
    [Fact]
    public async Task Takeover_DuringTheOldConnectionsWorldTransfer_GivesTheNewConnectionItsOwnTransfer_AndReleasesTheOldCapture()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = new()
        {
            IdleTimeout = TimeSpan.FromSeconds(10),
            SendStallTimeout = TimeSpan.FromSeconds(5),
            TimeProvider = clock,
            SnapshotIntervalTicks = 1
        };
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: true);
        DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        // The carrier and the first chunk reach the old client; the next write never completes.
        StallAfterTransport oldTransport = new("connection-old", allowedSends: 2);
        StallAfterTransport newTransport = new("connection-new", allowedSends: int.MaxValue);
        ServerConnection oldConnection = new(oldTransport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options) { WorldChunkBytes = 512 };
        ServerConnection newConnection = new(newTransport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options) { WorldChunkBytes = 512 };
        service.AttachConnection(oldConnection);
        service.AttachConnection(newConnection);
        try
        {
            host.HandleClientMessage("connection-old", new ClientJoinRequestMessage("session-a", "Player A"));
            await AdvanceUntilAsync(service, () => oldTransport.Stalled);
            WorldTransferSender oldTransfer = Assert.IsType<WorldTransferSender>(ActiveTransferField.GetValue(oldConnection));
            Assert.False(oldTransfer.IsComplete);
            Assert.False(oldTransfer.Capture.IsReleased);
            Assert.Equal(1, oldTransfer.TransferId);

            clock.Advance(DedicatedServerHost.SameSessionTakeoverSilence);
            host.HandleClientMessage("connection-new", new ClientJoinRequestMessage("session-a", "Player A"));
            await AdvanceUntilAsync(service, () => service.ConnectionCount == 1 && newConnection.WorldTransfers == 1);
            Assert.True(await newConnection.FlushOutboundAsync(Deadline));
            // A few more ticks so the new connection's snapshots follow its transfer.
            for (int i = 0; i < 4; i++)
            {
                await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            }

            Assert.True(await newConnection.FlushOutboundAsync(Deadline));

            Assert.True(oldTransport.IsDisposed, "the taken-over connection's transport was not closed");
            Assert.True(oldTransfer.Capture.IsReleased, "the old transfer's pooled capture was not released");
            Assert.Null(ActiveTransferField.GetValue(oldConnection));
            Assert.Equal(1, oldConnection.WorldTransfers);
            Assert.False(newConnection.IsFaulted);

            // The new wire: exactly one transfer, id 1, chunks 0..n-1 in order and all of it.
            IReadOnlyList<IProtocolMessage> wire = Decode(newTransport.Frames);
            JoinAcceptedMessage carrier = Assert.IsType<JoinAcceptedMessage>(Assert.Single(wire, message => message is JoinAcceptedMessage));
            Assert.Equal(1, carrier.WorldTransferId);
            Assert.Null(carrier.InitialWorld);
            WorldChunkMessage[] chunks = [.. wire.OfType<WorldChunkMessage>()];
            Assert.True(chunks.Length >= 3, $"only {chunks.Length} chunks");
            Assert.All(chunks, chunk => Assert.Equal(1, chunk.TransferId));
            Assert.Equal(Enumerable.Range(0, chunks.Length), chunks.Select(chunk => chunk.ChunkIndex));
            Assert.True(chunks[^1].IsFinal);
            Assert.Single(chunks, chunk => chunk.IsFinal);

            // The old wire, as far as it got: the same transfer id is its own (per connection).
            IReadOnlyList<IProtocolMessage> oldWire = Decode(oldTransport.Frames);
            Assert.Equal(1, Assert.IsType<JoinAcceptedMessage>(oldWire[0]).WorldTransferId);
            Assert.False(Assert.IsType<WorldChunkMessage>(oldWire[1]).IsFinal);

            List<IServerToClientMessage> delivered = WorldTransferReassembler.Reassemble(wire);
            JoinAcceptedMessage accepted = Assert.IsType<JoinAcceptedMessage>(delivered[0]);
            WorldSnapshot world = Assert.IsType<WorldSnapshot>(accepted.InitialWorld);
            using ClientWorldSession clientWorld = new(world.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            ClientJoinAcceptedApplier.ApplyToRuntime(clientWorld, accepted);
            foreach (ServerSnapshotMessage snapshot in delivered.OfType<ServerSnapshotMessage>())
            {
                RemoteWorldMutationApplier.ApplySnapshotToRuntime(clientWorld, snapshot.Snapshot);
            }

            Assert.Equal(WorldStateChecksum.Compute(host.Runtime!.Map), WorldStateChecksum.Compute(clientWorld.Map));
            output.WriteLine($"old transfer stalled after {oldTransport.SendsRecorded} frame(s); new transfer {chunks.Length} chunks, {newConnection.BytesSent:N0} B");
        }
        finally
        {
            await service.DisposeAsync();
            host.Dispose();
        }
    }

    // A generated terrain cube is hit for the first time while a join world (captured before the
    // hit, so the cube is in it without an id) is still going out. The CubeIdentified delta goes
    // out between two chunks; the client holds it, applies it after the world is assembled, and
    // ends on the server's world and cube checksums with the cube keyed by the new id.
    [Fact]
    public async Task CubeIdentified_ArrivingMidTransfer_IsAppliedAfterReassembly_AndTheChecksumsMatch()
    {
        const int terrainFloorSize = 32;
        PlayerSessionId session = new("session-a");
        using WorldSessionRuntime server = new(terrainFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost authority = new(server);
        JoinAcceptedMessage join = authority.JoinSession(session.Value, "A");
        WorldSnapshot joinWorld = Assert.IsType<WorldSnapshot>(join.InitialWorld);
        Assert.True(WorldSnapshotCapture.TryGetCapture(joinWorld, out _), "the join world is not a pooled capture view");

        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        GatedTransport transport = new("mid-transfer-id");
        await using ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness)
        {
            WorldChunkBytes = 1024
        };
        transport.Close();
        connection.Enqueue(join);
        Assert.True(await transport.SendStarted.WaitAsync(Deadline));

        // The hit, on the tick, while the writer is blocked on the carrier.
        Cube terrainCube = FindExposedTerrainCube(server.Map, out GeometryCellKey cell);
        Assert.False(server.Map.TryGetReplicatedId(terrainCube, out _));
        Assert.Contains(joinWorld.PlacedCubes!, cube => cube.CellPosition == new Vector3(cell.X, cell.Y, cell.Z) && cube.ActorId == null);
        int cubeId = HitFromAbove(server, session, cell);
        server.AdvanceAuthoritativeTick(
            WorldSessionRuntime.WorldChecksumIntervalTicks,
            SimulationClock.FixedDeltaSeconds,
            (PlayerCommandFrame?)null,
            PlayerSimulationTuning.AuthoritativeDefault);
        ServerSnapshotEnvelope snapshot = server.ExportServerSnapshotEnvelope();
        Assert.NotNull(snapshot.WorldChecksum);
        WorldMutationDelta identified = Assert.Single(snapshot.WorldMutations, mutation => mutation.Kind == WorldMutationKind.CubeIdentified);
        Assert.Equal(cubeId, identified.ActorId);
        Assert.True(identified.Sequence > joinWorld.MutationSequence, $"delta {identified.Sequence} vs world {joinWorld.MutationSequence}");
        connection.EnqueueSnapshot(new ServerSnapshotMessage(snapshot));
        transport.Open();
        Assert.True(await connection.FlushOutboundAsync(Deadline));

        IReadOnlyList<IProtocolMessage> wire = Decode(transport.Frames);
        List<IProtocolMessage> ordered = [.. wire];
        int snapshotIndex = ordered.FindIndex(message => message is ServerSnapshotMessage);
        int lastChunk = ordered.FindLastIndex(message => message is WorldChunkMessage);
        Assert.True(snapshotIndex > 1 && snapshotIndex < lastChunk, $"the CubeIdentified snapshot went out at {snapshotIndex}, the last chunk at {lastChunk}");

        List<IServerToClientMessage> delivered = WorldTransferReassembler.Reassemble(wire);
        JoinAcceptedMessage accepted = Assert.IsType<JoinAcceptedMessage>(delivered[0]);
        ServerSnapshotMessage held = Assert.IsType<ServerSnapshotMessage>(Assert.Single(delivered.Skip(1)));
        using ClientWorldSession client = new(terrainFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        ClientJoinAcceptedApplier.ApplyToRuntime(client, accepted);
        Cube clientCube = Assert.IsType<Cube>(client.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
        Assert.False(client.Map.TryGetReplicatedId(clientCube, out _));
        RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, held.Snapshot);

        Assert.False(RemoteWorldMutationApplier.IsWorldOutOfSync(client, held.Snapshot));
        Assert.True(client.Map.TryGetReplicatedId(clientCube, out int clientId));
        Assert.Equal(cubeId, clientId);
        SimulationStateChecksum.Parts serverParts = SimulationStateChecksum.ComputeParts(server.Map, [], 0);
        SimulationStateChecksum.Parts clientParts = SimulationStateChecksum.ComputeParts(client.Map, [], 0);
        Assert.Equal(serverParts.World, clientParts.World);
        Assert.Equal(serverParts.Cubes, clientParts.Cubes);
        Assert.Equal(WorldStateChecksum.Compute(server.Map), WorldStateChecksum.Compute(client.Map));
        output.WriteLine($"cube {cubeId} at {cell}; snapshot at frame {snapshotIndex}, last chunk at frame {lastChunk}");
    }

    // NETR-1 overflow resyncs while a SCALE-1 transfer is in flight on a slow link. Each frame write
    // takes 40 ms and every queued snapshot overflows the delta cap, so the service asks for a resync
    // whenever the 1.5 s cooldown allows. Transfers must never overlap on the wire (the client
    // reassembler would throw on a second carrier), and once the link is fast and the world is quiet
    // every queued transfer drains and no new one starts. Fix loop 1 (tester B, reviewer S6): an
    // overflow while a transfer is queued or going out is deferred until it finished, so no full
    // world is ever queued behind an unfinished one (before: 6 transfers in 8 s, 5 redundant).
    [Fact]
    public async Task OverflowResyncs_DuringAnInFlightTransfer_AreSentOneAfterAnother_AndConverge()
    {
        WorldSnapshot resyncWorld = WorldTransferProtocolTests.CreateWorld(cubes: 3_000, rubble: 20, subcells: 0);
        BigWorldMutatingAuthority authority = new(resyncWorld) { MutationsPerTick = 20 };
        DedicatedServerHost host = CreateHost(authority);
        ServerConnectionOptions options = WithoutLiveness with { MaxCoalescedWorldMutations = 64, SnapshotIntervalTicks = 1 };
        DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        SlowSendTransport transport = new("slow-link") { SendDelay = TimeSpan.FromMilliseconds(40) };
        ServerConnection connection = new(transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options) { WorldChunkBytes = 1024 };
        service.AttachConnection(connection);
        try
        {
            host.HandleClientMessage("slow-link", new ClientJoinRequestMessage("session-a", "Player A"));
            Assert.True(host.IsConnectionJoined("slow-link"));

            // Phase 1: 8 s of a busy world over the slow link.
            System.Diagnostics.Stopwatch phase = System.Diagnostics.Stopwatch.StartNew();
            int maxQueuedWorlds = 0;
            while (phase.Elapsed < TimeSpan.FromSeconds(8))
            {
                await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
                maxQueuedWorlds = Math.Max(maxQueuedWorlds, authority.WorldResyncs - (int)connection.WorldTransfers);
                await Task.Delay(TimeSpan.FromSeconds(SimulationClock.FixedDeltaSeconds));
            }

            int servedInPhase1 = authority.WorldResyncs;
            long startedInPhase1 = connection.WorldTransfers;
            long overflowsInPhase1 = connection.OverflowResyncs;
            output.WriteLine(
                $"phase 1 (8 s, 40 ms/frame): {overflowsInPhase1} overflows, {servedInPhase1} resyncs served, {startedInPhase1} transfers started, " +
                $"up to {maxQueuedWorlds} full worlds queued behind an unfinished transfer, {connection.WorldChunksSent} chunks sent");
            Assert.True(servedInPhase1 >= 2, $"only {servedInPhase1} resyncs were served; the scenario did not overflow during a transfer");
            Assert.False(connection.IsFaulted, connection.CloseReason.ToString());
            ServerMetricsSnapshot metrics = service.CaptureMetrics();
            output.WriteLine(
                $"metrics: {metrics.CoalesceOverflowResyncs} overflow resyncs served, {metrics.CoalesceOverflowResyncsDeferred} deferred behind a transfer, " +
                $"{metrics.BackToBackOverflowResyncs} served right after one, {metrics.BackToBackWorldTransfers} transfers queued behind another");
            // At most the one resync just served and not yet started by the writer; never a second
            // full world queued behind an unfinished transfer.
            Assert.InRange(maxQueuedWorlds, 0, 1);
            Assert.Equal(0, connection.BackToBackWorldTransfers);
            Assert.Equal(0, metrics.BackToBackWorldTransfers);
            Assert.True(metrics.CoalesceOverflowResyncsDeferred >= 1, "no overflow happened while a transfer was in flight");
            Assert.InRange(metrics.BackToBackOverflowResyncs, 1, metrics.CoalesceOverflowResyncsDeferred);

            // Phase 2: the link recovers and the world goes quiet.
            authority.MutationsPerTick = 0;
            transport.SendDelay = TimeSpan.Zero;
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            long lastTransfers = -1;
            int stableRounds = 0;
            while (stableRounds < 20)
            {
                Assert.True(DateTime.UtcNow < deadline, $"never converged: {authority.WorldResyncs} served, {connection.WorldTransfers} started");
                await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
                Assert.True(await connection.FlushOutboundAsync(Deadline));
                stableRounds = connection.WorldTransfers == lastTransfers && connection.WorldTransfers == authority.WorldResyncs ? stableRounds + 1 : 0;
                lastTransfers = connection.WorldTransfers;
                await Task.Delay(TimeSpan.FromSeconds(SimulationClock.FixedDeltaSeconds));
            }

            Assert.False(connection.IsFaulted);
            Assert.Equal(authority.WorldResyncs, connection.WorldTransfers);
            Assert.Equal(0, connection.BackToBackWorldTransfers);

            // The client side: one carrier at a time, every transfer completes, ids 1..n in order.
            IReadOnlyList<IProtocolMessage> wire = Decode(transport.Frames);
            List<IServerToClientMessage> delivered = WorldTransferReassembler.Reassemble(wire);
            int carriers = wire.Count(message => message is ServerSnapshotMessage { WorldTransferId: not null });
            int finals = wire.Count(message => message is WorldChunkMessage { IsFinal: true });
            Assert.Equal(connection.WorldTransfers, carriers);
            Assert.Equal(carriers, finals);
            Assert.Equal(
                Enumerable.Range(1, carriers),
                wire.OfType<ServerSnapshotMessage>().Where(message => message.WorldTransferId != null).Select(message => message.WorldTransferId!.Value));
            int deliveredWorlds = delivered.Count(message => message is ServerSnapshotMessage { Snapshot.World: not null });
            Assert.Equal(carriers, deliveredWorlds);
            output.WriteLine(
                $"converged: {connection.WorldTransfers} full-world transfers in total, {connection.BackToBackWorldTransfers} queued behind an unfinished one, " +
                $"{connection.WorldChunksSent} chunks, {connection.BytesSent:N0} B");
        }
        finally
        {
            await service.DisposeAsync();
            host.Dispose();
        }
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

    private static Cube FindExposedTerrainCube(Map map, out GeometryCellKey cell)
    {
        foreach (Cube candidate in map.SolidCubes.OrderBy(cube => cube.Position.X).ThenBy(cube => cube.Position.Z).ThenBy(cube => cube.Position.Y))
        {
            if (!map.TryGetCellForCube(candidate, out GeometryCellKey key) ||
                key.Y < 1 ||
                key.Y + 2 >= map.Height ||
                map.TryGetCubeAtCell(key.X, key.Y + 1, key.Z) != null ||
                map.TryGetCubeAtCell(key.X, key.Y + 2, key.Z) != null ||
                map.TryGetReplicatedId(candidate, out _))
            {
                continue;
            }

            cell = key;
            return candidate;
        }

        throw new InvalidOperationException("The generated terrain has no exposed cube above the floor.");
    }

    private static int HitFromAbove(WorldSessionRuntime runtime, PlayerSessionId session, GeometryCellKey cell)
    {
        runtime.Enemies.Clear();
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        Vector3 origin = new(cell.X + 0.5f, cell.Y + 2.2f, cell.Z + 0.5f);
        runtime.Player.SetWorldPosition(origin);
        runtime.Player.SetFacing(-Vector3.UnitY);
        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(session, 1, new CombatUseIntent(origin, -Vector3.UnitY)));
        Assert.True(result.Accepted, result.Rejection?.ToString());
        CombatResolutionResult combat = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.True(combat.DamageApplied > 0, $"The hit missed: target {combat.TargetActorId}.");
        Assert.False(combat.TargetDied);
        int cubeId = combat.TargetActorId ?? throw new InvalidOperationException("The hit has no target id.");
        Assert.True(ReplicatedActorIdRanges.IsWorldObjectId(cubeId));
        return cubeId;
    }

    // Records frames; lets the first allowedSends writes through, then every write waits until the
    // transport is disposed (a peer that stopped reading) and fails.
    internal sealed class StallAfterTransport(string connectionId, int allowedSends) : ITransportConnection
    {
        private readonly object _sync = new();
        private readonly List<byte[]> _frames = [];
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sends;

        public string ConnectionId => connectionId;

        public bool IsDisposed => _disposed.Task.IsCompleted;

        public bool Stalled => Volatile.Read(ref _sends) > allowedSends;

        public int SendsRecorded
        {
            get
            {
                lock (_sync)
                {
                    return _frames.Count;
                }
            }
        }

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

        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (Interlocked.Increment(ref _sends) > allowedSends)
            {
                await _disposed.Task;
                throw new ObjectDisposedException(nameof(StallAfterTransport));
            }

            lock (_sync)
            {
                _frames.Add(payload.ToArray());
            }
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

    // Records frames; every write takes SendDelay.
    internal sealed class SlowSendTransport(string connectionId) : ITransportConnection
    {
        private readonly object _sync = new();
        private readonly List<byte[]> _frames = [];
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();

        public string ConnectionId => connectionId;

        public TimeSpan SendDelay { get; set; }

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

        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            TimeSpan delay = SendDelay;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            lock (_sync)
            {
                _frames.Add(payload.ToArray());
            }
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] next = await _inbound.Reader.ReadAsync(cancellationToken);
            next.CopyTo(buffer);
            return next.Length;
        }

        public ValueTask DisposeAsync()
        {
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    // MutatingAuthority with a large resync world, so a resync is a multi-chunk transfer.
    private sealed class BigWorldMutatingAuthority(WorldSnapshot resyncWorld) : ITickDrivenAuthoritativeSessionHost, IWorldResyncSource
    {
        private ulong _nextMutationSequence = 1;
        private int _worldResyncs;

        public int MutationsPerTick { get; set; } = 1;

        public int WorldResyncs => Volatile.Read(ref _worldResyncs);

        public ulong CurrentServerTick { get; private set; }

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            return new JoinAcceptedMessage(requestedSessionId, 101, CurrentServerTick, InitialWorld: null);
        }

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
        {
            return new AuthoritativeActionResult(message.SessionId.Value, message.ActionSequence, CurrentServerTick, true, null);
        }

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false)
        {
            WorldMutationDelta[] mutations = new WorldMutationDelta[MutationsPerTick];
            for (int i = 0; i < mutations.Length; i++)
            {
                mutations[i] = Mutation(_nextMutationSequence++);
            }

            return CreateSnapshot(CurrentServerTick, new Dictionary<string, ulong> { ["session-a"] = CurrentServerTick }, mutations);
        }

        public ServerSnapshotEnvelope ExportWorldResync()
        {
            Interlocked.Increment(ref _worldResyncs);
            return CreateSnapshot(CurrentServerTick, new Dictionary<string, ulong>(), Array.Empty<WorldMutationDelta>()) with
            {
                World = resyncWorld
            };
        }

        public IReadOnlyList<AuthoritativeActionResult> AdvanceTick(
            ulong serverTick,
            float deltaTime,
            IReadOnlyList<PlayerCommandFrame> commandFrames,
            IReadOnlyList<QueuedPlayerAction> queuedActions,
            PlayerSimulationTuning playerTuning,
            bool allowGameplaySimulation = true)
        {
            CurrentServerTick = serverTick;
            return Array.Empty<AuthoritativeActionResult>();
        }
    }
}
