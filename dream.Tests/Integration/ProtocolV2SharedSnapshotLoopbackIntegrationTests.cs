using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Multiplayer.Protocol;

namespace Dream.Tests.Integration;

// WIRE-5, WIRE-6 and NET-6 together over real loopback TCP: the real DedicatedServerService and
// a runtime-backed host, ticked by the test one fixed step at a time, with raw socket clients
// that keep every frame's bytes. The clients speak protocol v2 by hand, so the test sees exactly
// what the server puts on the wire: one snapshot payload serialized once and wrapped in each
// connection's own envelope, slimmed to v2, with actors in canonical order. Every client also
// applies what it receives into a ClientWorldSession, like PlayScene, and checks the world
// checksums the server sends.
public class ProtocolV2SharedSnapshotLoopbackIntegrationTests(ITestOutputHelper output)
{
    private const string SnapshotKind = "server.snapshot";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Join order is deliberately not ordinal: the primary seat goes to the first joiner, and the
    // two secondaries join in reverse ordinal order.
    private const string PrimarySession = "session-m";
    private const string LateSecondary = "session-b";
    private const string EarlySecondary = "session-z";

    // Cells within the primary's build reach of its spawn at (5, 1.5, 5).
    private static readonly Vector3[] BuildCells =
    [
        new(7f, 1f, 5f), new(3f, 1f, 5f), new(5f, 1f, 7f), new(5f, 1f, 3f),
        new(7f, 1f, 6f), new(3f, 1f, 4f), new(6f, 1f, 7f), new(4f, 1f, 3f)
    ];

    [Fact]
    public async Task ThreeClients_ReceiveEverySnapshotOnce_SharedPayloadBytes_GapFreeEnvelopes_AndMatchingWorlds()
    {
        await using ServerUnderTest server = await ServerUnderTest.StartAsync();
        await using RawClient primary = await server.ConnectAndJoinAsync(PrimarySession);
        await using RawClient early = await server.ConnectAndJoinAsync(EarlySecondary);
        await using RawClient late = await server.ConnectAndJoinAsync(LateSecondary);
        RawClient[] clients = [primary, early, late];

        const int Ticks = 200;
        int builds = 0;
        for (int i = 0; i < Ticks; i++)
        {
            // The primary stays within build reach of its spawn; the secondaries walk.
            await primary.SendCommandAsync(walking: false);
            await early.SendCommandAsync(walking: i % 60 < 20);
            await late.SendCommandAsync(walking: i % 50 < 10);

            if (i % 25 == 5 && builds < BuildCells.Length)
            {
                await primary.SendActionAsync(new BuildUseIntent(BuildCells[builds++]));
            }

            await server.TickAsync();
            // Every writer drains before the next tick, so no snapshot is ever merged and every
            // connection sends every tick's shared payload.
            Assert.True(await server.FlushAllAsync(), "A connection did not drain its queue.");
        }

        ulong lastTick = server.Host.Runtime!.CurrentServerTick;
        foreach (RawClient client in clients)
        {
            await client.WaitForSnapshotTickAsync(lastTick);
        }

        foreach (RawClient client in clients)
        {
            Assert.Null(client.ReadFault);
            Assert.True(client.Frames.Count > Ticks / 2, $"{client.SessionId} received {client.Frames.Count} frames.");
            Assert.Equal(0, server.ConnectionFor(client).CoalescedSnapshots);
            AssertEnvelopeSequencesAreGapFree(client);
            AssertSlimV2Payloads(client);
            AssertCanonicalActorOrder(client);
        }

        // The same tick's snapshot carries byte-identical payloads on every connection, while each
        // envelope (its sequence) is the connection's own.
        Dictionary<ulong, Frame>[] snapshotsByTick = clients.Select(SnapshotsByTick).ToArray();
        ulong[] sharedTicks = snapshotsByTick
            .Select(map => map.Keys)
            .Aggregate((IEnumerable<ulong>)snapshotsByTick[0].Keys, (common, keys) => common.Intersect(keys))
            .Order()
            .ToArray();
        Assert.True(sharedTicks.Length >= Ticks - 5, $"Only {sharedTicks.Length} ticks reached every client.");
        int differingEnvelopes = 0;
        foreach (ulong tick in sharedTicks)
        {
            Frame reference = snapshotsByTick[0][tick];
            for (int c = 1; c < clients.Length; c++)
            {
                Frame other = snapshotsByTick[c][tick];
                Assert.True(
                    reference.PayloadJson.AsSpan().SequenceEqual(other.PayloadJson),
                    $"tick {tick}: {clients[c].SessionId}'s payload differs from {clients[0].SessionId}'s.");
                differingEnvelopes += reference.Decoded.Envelope.Sequence != other.Decoded.Envelope.Sequence ? 1 : 0;
            }
        }

        // The joins happened at different points, so the per-connection sequences really differ.
        Assert.True(differingEnvelopes > 0, "Every connection used the same envelope sequences.");

        // The world changed, and every client rebuilt the server's world from the stream.
        Assert.True(primary.AcceptedActions > 0, "No build was accepted.");
        ulong serverChecksum = WorldStateChecksum.Compute(server.Host.Runtime!.Map);
        foreach (RawClient client in clients)
        {
            ClientWorldReplay replay = client.Replay(server.Host.Runtime!.Map.Width);
            Assert.True(replay.ChecksumsVerified >= 2, $"{client.SessionId} verified {replay.ChecksumsVerified} checksums.");
            Assert.Equal(0, replay.ChecksumMismatches);
            Assert.Equal(serverChecksum, replay.FinalChecksum);
            Assert.True(replay.MutationsApplied >= primary.AcceptedActions, $"{client.SessionId} applied {replay.MutationsApplied} mutations.");
        }

        output.WriteLine(
            $"ticks={lastTick} sharedTicks={sharedTicks.Length} builds={primary.AcceptedActions} " +
            $"frames=[{string.Join(",", clients.Select(client => client.Frames.Count))}]");
    }

    [Fact]
    public async Task SlowClient_GetsMergedSnapshotsEncodedForItsConnection_ThatDecode_WithMutationsInOrder()
    {
        await using ServerUnderTest server = await ServerUnderTest.StartAsync();
        await using RawClient primary = await server.ConnectAndJoinAsync(PrimarySession);
        await using RawClient fast = await server.ConnectAndJoinAsync(EarlySecondary);
        // A tiny receive window and a paused reader: the server's writer for this connection
        // blocks in the socket send, and its queued snapshots merge behind the blocked one.
        await using RawClient slow = await server.ConnectAndJoinAsync(LateSecondary, receiveBufferSize: 1024);
        ServerConnection slowConnection = server.ConnectionFor(slow);
        ulong slowJoinTick = server.Host.Runtime!.CurrentServerTick;
        slow.PauseReading();

        int builds = 0;
        int tick = 0;
        int ticksAfterFirstMerge = 0;
        while (ticksAfterFirstMerge < 120)
        {
            Assert.True(tick < 6000, $"The slow connection never coalesced (pending {slowConnection.PendingOutboundCount}).");
            await primary.SendCommandAsync(walking: false);
            await fast.SendCommandAsync(walking: tick % 90 < 30);
            // Builds keep coming while the slow client lags, so merged snapshots carry mutations.
            if (tick % 15 == 3 && builds < BuildCells.Length && (builds < 2 || slowConnection.CoalescedSnapshots > 0))
            {
                await primary.SendActionAsync(new BuildUseIntent(BuildCells[builds++]));
            }

            await server.TickAsync();
            Assert.True(await server.ConnectionFor(primary).FlushOutboundAsync(Timeout));
            Assert.True(await server.ConnectionFor(fast).FlushOutboundAsync(Timeout));
            Assert.False(slowConnection.IsFaulted, "The slow connection faulted instead of coalescing.");
            ticksAfterFirstMerge += slowConnection.CoalescedSnapshots > 0 ? 1 : 0;
            tick++;
        }

        Assert.Equal(BuildCells.Length, builds);
        slow.ResumeReading();
        Assert.True(await slowConnection.FlushOutboundAsync(Timeout), "The slow connection never drained.");
        ulong lastTick = server.Host.Runtime!.CurrentServerTick;
        await slow.WaitForSnapshotTickAsync(lastTick);
        await fast.WaitForSnapshotTickAsync(lastTick);
        await primary.WaitForSnapshotTickAsync(lastTick);

        long coalesced = slowConnection.CoalescedSnapshots;
        Assert.True(coalesced > 0);
        Assert.Null(slow.ReadFault);
        AssertEnvelopeSequencesAreGapFree(slow);
        AssertSlimV2Payloads(slow);
        AssertCanonicalActorOrder(slow);

        List<Frame> slowSnapshots = slow.Snapshots().ToList();
        List<Frame> fastSnapshots = fast.Snapshots().ToList();
        // Every merged-away tick is missing from the slow stream, and nothing else is.
        int fastSinceSlowJoined = fastSnapshots.Count(frame => TickOf(frame) > slowJoinTick);
        Assert.Equal(0, server.ConnectionFor(fast).CoalescedSnapshots);
        Assert.Equal(fastSinceSlowJoined - coalesced, slowSnapshots.Count);
        Assert.All(slowSnapshots, frame => Assert.True(TickOf(frame) > slowJoinTick));

        // Merged snapshots were encoded for this connection alone: their payload matches no
        // shared payload, yet re-encoding the decoded message reproduces it exactly.
        Dictionary<ulong, Frame> fastByTick = SnapshotsByTick(fast);
        int merged = 0;
        int mergedMutations = 0;
        TransportJsonCodec codec = new();
        foreach (Frame frame in slowSnapshots)
        {
            ServerSnapshotEnvelope snapshot = ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot;
            Frame shared = fastByTick[snapshot.ServerTick];
            if (!frame.PayloadJson.AsSpan().SequenceEqual(shared.PayloadJson))
            {
                merged++;
                mergedMutations += snapshot.WorldMutations.Count;
                byte[] reencoded = codec.Encode(frame.Decoded.Envelope, (ServerSnapshotMessage)frame.Decoded.Message);
                Assert.True(
                    reencoded.AsSpan().SequenceEqual(frame.Payload),
                    $"tick {snapshot.ServerTick}: the merged snapshot does not round-trip byte for byte.");
            }
        }

        Assert.True(merged > 0, "No merged snapshot reached the wire.");
        Assert.True(mergedMutations > 0, "No merged snapshot carried a world mutation.");

        // Mutations and damage arrive in the same order, none lost or repeated, merged or not.
        string[] fastMutations = MutationStream(fast, after: slowJoinTick);
        string[] slowMutations = MutationStream(slow, after: slowJoinTick);
        Assert.Equal(fastMutations, slowMutations);
        ulong[] sequences = slow.Snapshots()
            .SelectMany(frame => ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot.WorldMutations)
            .Select(mutation => mutation.Sequence)
            .ToArray();
        Assert.True(sequences.Length > 0, "No mutation reached the slow client.");
        for (int i = 1; i < sequences.Length; i++)
        {
            Assert.True(sequences[i] > sequences[i - 1], $"mutation {sequences[i]} arrived after {sequences[i - 1]}");
        }

        Assert.Equal(DamageStream(fast, slowJoinTick), DamageStream(slow, slowJoinTick));

        // The lagging client still converges on the server's world.
        ClientWorldReplay replay = slow.Replay(server.Host.Runtime!.Map.Width);
        Assert.Equal(0, replay.ChecksumMismatches);
        Assert.Equal(WorldStateChecksum.Compute(server.Host.Runtime!.Map), replay.FinalChecksum);

        output.WriteLine(
            $"ticks={lastTick} coalesced={coalesced} slowSnapshots={slowSnapshots.Count} fastSnapshots={fastSnapshots.Count} " +
            $"mergedOnWire={merged} mergedMutations={mergedMutations} mutations={sequences.Length} builds={primary.AcceptedActions}");
    }

    [Fact]
    public async Task V1Client_IsRejectedWithALoggedReason_WithoutAnUnobservedTaskFault_AndTheOtherClientsKeepPlaying()
    {
        const string ExpectedReason = "Client uses protocol v1; this server uses v2.";
        List<Exception> unobserved = [];
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, args) =>
        {
            // The handler is process-wide; keep only faults caused by this test's v1 frame.
            if (args.Exception.Flatten().InnerExceptions.Any(inner => inner.ToString().Contains("protocol version 1", StringComparison.OrdinalIgnoreCase)))
            {
                lock (unobserved)
                {
                    unobserved.Add(args.Exception);
                }
            }
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;
        try
        {
            await using ServerUnderTest server = await ServerUnderTest.StartAsync();
            List<ServerConnectionError> connectionErrors = [];
            server.Service.ConnectionError += error =>
            {
                lock (connectionErrors)
                {
                    connectionErrors.Add(error);
                }
            };
            await using RawClient a = await server.ConnectAndJoinAsync(PrimarySession);
            await using RawClient b = await server.ConnectAndJoinAsync(EarlySecondary);
            for (int i = 0; i < 20; i++)
            {
                await a.SendCommandAsync(walking: true);
                await b.SendCommandAsync(walking: false);
                await server.TickAsync();
                Assert.True(await server.FlushAllAsync());
            }

            // A protocol v1 client: v1 serializer options (explicit nulls and messageKind in the
            // payload) and protocolVersion 1 in the envelope, framed exactly like a v2 frame.
            ClientHelloMessage hello = new("legacy-v1-client");
            byte[] v1Hello = JsonSerializer.SerializeToUtf8Bytes(
                new { protocolVersion = 1, messageKind = hello.MessageKind, sequence = 1UL, payload = hello },
                ProtocolWireOptions.V1);
            InvalidOperationException versionError = Assert.Throws<InvalidOperationException>(() => new TransportJsonCodec().Decode(v1Hello));
            Assert.Equal("Unsupported protocol version 1. Supported version: 2.", versionError.Message);

            using TcpClient legacy = new();
            await legacy.ConnectAsync(IPAddress.Loopback, server.Port);
            ServerConnection legacyConnection = await server.AcceptAsync();
            Assert.Equal(3, server.Service.ConnectionCount);
            NetworkStream legacyStream = legacy.GetStream();
            await legacyStream.WriteAsync(new LengthPrefixedMessageFramer().Frame(v1Hello));

            // The server reads the frame, fails to decode it, answers with one v2 rejection that
            // names both versions, and closes only that connection.
            using MemoryStream received = new();
            byte[] buffer = new byte[4096];
            using (CancellationTokenSource readTimeout = new(Timeout))
            {
                while (true)
                {
                    int read;
                    try
                    {
                        read = await legacyStream.ReadAsync(buffer, readTimeout.Token);
                    }
                    catch (IOException)
                    {
                        // A reset is also a close.
                        break;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    received.Write(buffer, 0, read);
                }
            }

            byte[] receivedBytes = received.ToArray();
            Assert.True(
                new LengthPrefixedMessageFramer().TryDeframe(receivedBytes, out byte[]? rejectionPayload, out int consumed),
                $"expected one complete rejection frame, got {receivedBytes.Length} bytes");
            Assert.Equal(receivedBytes.Length, consumed);
            DecodedProtocolMessage rejection = new TransportJsonCodec().Decode(rejectionPayload!);
            Assert.Equal(TransportEnvelope.CurrentProtocolVersion, rejection.Envelope.ProtocolVersion);
            Assert.Equal(1UL, rejection.Envelope.Sequence);
            Assert.Equal(ExpectedReason, Assert.IsType<JoinRejectedMessage>(rejection.Message).Reason);

            await WaitUntilAsync(() => server.Service.ConnectionCount == 2, "the v1 connection was never dropped");
            Assert.False(server.Host.IsConnectionJoined(legacyConnection.ConnectionId));

            // The server logged the reason once, for that connection only (its id is the remote
            // endpoint), as a protocol mismatch rather than a generic error.
            lock (connectionErrors)
            {
                ServerConnectionError logged = Assert.Single(connectionErrors);
                Assert.Equal(legacyConnection.ConnectionId, logged.ConnectionId);
                Assert.EndsWith($":{((IPEndPoint)legacy.Client.LocalEndPoint!).Port}", logged.ConnectionId);
                Assert.Equal("protocol-mismatch", logged.Kind);
                Assert.Equal(ExpectedReason, logged.Reason);
                ProtocolVersionMismatchException mismatch = Assert.IsType<ProtocolVersionMismatchException>(logged.Exception);
                Assert.Equal(1, mismatch.PeerVersion);
                Assert.Equal(versionError.Message, mismatch.InnerException!.Message);
            }

            // The connection loop task must not end faulted: force the finalizers that would
            // report an unobserved task exception and check that none came from this connection.
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(20);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            lock (unobserved)
            {
                Assert.Empty(unobserved);
            }

            // The server keeps ticking and the v2 clients keep receiving every snapshot.
            int framesBefore = a.Frames.Count;
            for (int i = 0; i < 30; i++)
            {
                await a.SendCommandAsync(walking: true);
                await b.SendCommandAsync(walking: true);
                await server.TickAsync();
                Assert.True(await server.FlushAllAsync());
            }

            ulong lastTick = server.Host.Runtime!.CurrentServerTick;
            await a.WaitForSnapshotTickAsync(lastTick);
            await b.WaitForSnapshotTickAsync(lastTick);
            Assert.True(a.Frames.Count >= framesBefore + 30);
            foreach (RawClient client in new[] { a, b })
            {
                Assert.Null(client.ReadFault);
                AssertEnvelopeSequencesAreGapFree(client);
                Assert.True(client.IsConnected);
            }

            lock (connectionErrors)
            {
                Assert.Single(connectionErrors);
            }

            // A v2 client can still join afterwards.
            await using RawClient c = await server.ConnectAndJoinAsync(LateSecondary);
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
            await c.WaitForSnapshotTickAsync(server.Host.Runtime!.CurrentServerTick);
            Assert.Null(c.ReadFault);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }
    }

    private static void AssertEnvelopeSequencesAreGapFree(RawClient client)
    {
        List<Frame> frames = client.Frames.ToList();
        for (int i = 0; i < frames.Count; i++)
        {
            Assert.Equal((ulong)(i + 1), frames[i].Decoded.Envelope.Sequence);
            Assert.Equal(TransportEnvelope.CurrentProtocolVersion, frames[i].Decoded.Envelope.ProtocolVersion);
            Assert.Equal(frames[i].Decoded.Message.MessageKind, frames[i].Decoded.Envelope.MessageKind);
        }

        Assert.IsType<ServerHelloMessage>(frames[0].Decoded.Message);
        Assert.IsType<JoinAcceptedMessage>(frames[1].Decoded.Message);
        ulong previousTick = 0;
        foreach (Frame frame in client.Snapshots())
        {
            ulong tick = ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot.ServerTick;
            Assert.True(tick > previousTick, $"{client.SessionId}: snapshot tick {tick} after {previousTick}");
            previousTick = tick;
        }
    }

    private static void AssertSlimV2Payloads(RawClient client)
    {
        foreach (Frame frame in client.Frames)
        {
            string payload = Encoding.UTF8.GetString(frame.PayloadJson);
            Assert.DoesNotContain("messageKind", payload, StringComparison.Ordinal);
            Assert.DoesNotContain(":null", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("effectiveRoster", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("effectiveInitialActors", payload, StringComparison.Ordinal);
            string expectedPrefix =
                $"{{\"protocolVersion\":2,\"messageKind\":\"{frame.Decoded.Envelope.MessageKind}\",\"sequence\":{frame.Decoded.Envelope.Sequence},\"payload\":";
            Assert.Equal(expectedPrefix, Encoding.UTF8.GetString(frame.Payload, 0, frame.PayloadOffset));
            Assert.Equal((byte)'}', frame.Payload[^1]);
            Assert.Equal(frame.Payload.Length - 1, frame.PayloadOffset + frame.PayloadJson.Length);
        }
    }

    // NET-6: secondaries are captured in ordinal session-id order whatever the join order. The
    // primary seat still comes first (it is whoever joined first), a documented gap.
    private static void AssertCanonicalActorOrder(RawClient client)
    {
        string[] expected = [PrimarySession, LateSecondary, EarlySecondary];
        int checkedSnapshots = 0;
        foreach (Frame frame in client.Snapshots())
        {
            string[] players = ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot.Actors
                .Where(actor => actor.Kind == CreatureKind.Player)
                .Select(actor => actor.SessionId!)
                .ToArray();
            if (players.Length < expected.Length)
            {
                continue;
            }

            Assert.Equal(expected, players);
            Assert.Equal(players.Skip(1).Order(StringComparer.Ordinal), players.Skip(1));
            checkedSnapshots++;
        }

        Assert.True(checkedSnapshots > 0, $"{client.SessionId} never saw all three players.");
    }

    private static Dictionary<ulong, Frame> SnapshotsByTick(RawClient client)
    {
        return client.Snapshots().ToDictionary(frame => ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot.ServerTick);
    }

    private static ulong TickOf(Frame frame)
    {
        return ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot.ServerTick;
    }

    private static string[] MutationStream(RawClient client, ulong after)
    {
        return client.Snapshots()
            .Select(frame => ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot)
            .Where(snapshot => snapshot.ServerTick > after)
            .SelectMany(snapshot => snapshot.WorldMutations)
            .Select(mutation =>
                $"{mutation.Sequence}:{mutation.Kind}:{mutation.CellPosition}:{mutation.TargetCellPosition}:" +
                $"{mutation.CenterPosition}:{mutation.MaterialId}:{mutation.ActorId}:{mutation.FracturePieces?.Count}")
            .ToArray();
    }

    private static string[] DamageStream(RawClient client, ulong after)
    {
        return client.Snapshots()
            .Select(frame => ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot)
            .Where(snapshot => snapshot.ServerTick > after)
            .SelectMany(snapshot => snapshot.DamageEvents)
            .Select(damage => damage.ToString())
            .ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(10);
        }
    }

    internal sealed record Frame(byte[] Payload, DecodedProtocolMessage Decoded, int PayloadOffset, byte[] PayloadJson);

    internal sealed record ClientWorldReplay(int ChecksumsVerified, int ChecksumMismatches, ulong FinalChecksum, int MutationsApplied);

    // The real service and host, ticked by the test instead of the service's own tick loop.
    private sealed class ServerUnderTest : IAsyncDisposable
    {
        private readonly Dictionary<RawClient, ServerConnection> _connections = [];

        private ServerUnderTest(DedicatedServerHost host, DedicatedServerService service)
        {
            Host = host;
            Service = service;
        }

        public DedicatedServerHost Host { get; }

        public DedicatedServerService Service { get; }

        public int Port => Service.Port;

        public static Task<ServerUnderTest> StartAsync()
        {
            DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
            DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer());
            service.Start();
            return Task.FromResult(new ServerUnderTest(host, service));
        }

        public ValueTask<ServerConnection> AcceptAsync()
        {
            return Service.AcceptNextClientAsync();
        }

        public async Task<RawClient> ConnectAndJoinAsync(string sessionId, int? receiveBufferSize = null)
        {
            Task<ServerConnection> accept = AcceptAsync().AsTask();
            RawClient client = await RawClient.ConnectAsync(Port, sessionId, receiveBufferSize);
            ServerConnection connection = await accept.WaitAsync(Timeout);
            _connections[client] = connection;
            await client.HandshakeAsync();
            await WaitUntilAsync(() => Host.IsConnectionJoined(connection.ConnectionId), $"{sessionId} never joined");
            await client.WaitForJoinAcceptedAsync();
            return client;
        }

        public ServerConnection ConnectionFor(RawClient client) => _connections[client];

        // One fixed tick per call; the clock's float accumulation may need a second nudge.
        public async Task TickAsync()
        {
            ulong before = Host.Runtime!.CurrentServerTick;
            int executed = await Service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            if (executed == 0)
            {
                executed = await Service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds * 0.01);
            }

            Assert.Equal(1, executed);
            Assert.Equal(before + 1, Host.Runtime!.CurrentServerTick);
        }

        public async Task<bool> FlushAllAsync()
        {
            bool flushed = true;
            foreach (ServerConnection connection in _connections.Values)
            {
                flushed &= await connection.FlushOutboundAsync(Timeout);
            }

            return flushed;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Host.Dispose();
        }
    }

    // A protocol v2 client written against the wire format directly: it encodes its own frames
    // and keeps every received frame's exact bytes.
    internal sealed class RawClient : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly TransportJsonCodec _codec = new();
        private readonly LengthPrefixedMessageFramer _framer = new();
        private readonly List<Frame> _frames = [];
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ManualResetEventSlim _readGate = new(initialState: true);
        private readonly Task _readLoop;
        private ulong _outboundSequence;
        private ulong _commandSequence;
        private ulong _actionSequence;

        private RawClient(TcpClient tcp, string sessionId)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            SessionId = sessionId;
            _readLoop = Task.Factory.StartNew(ReadLoop, TaskCreationOptions.LongRunning);
        }

        public string SessionId { get; }

        public Exception? ReadFault { get; private set; }

        public bool IsConnected => _tcp.Connected && ReadFault == null;

        public IReadOnlyList<Frame> Frames
        {
            get
            {
                lock (_frames)
                {
                    return _frames.ToArray();
                }
            }
        }

        public int AcceptedActions => Frames
            .Select(frame => frame.Decoded.Message)
            .OfType<ServerActionMessage>()
            .Count(action => action.Result.Accepted);

        public static async Task<RawClient> ConnectAsync(int port, string sessionId, int? receiveBufferSize)
        {
            TcpClient tcp = new() { NoDelay = true };
            if (receiveBufferSize.HasValue)
            {
                tcp.ReceiveBufferSize = receiveBufferSize.Value;
            }

            await tcp.ConnectAsync(IPAddress.Loopback, port);
            return new RawClient(tcp, sessionId);
        }

        public IEnumerable<Frame> Snapshots()
        {
            return Frames.Where(frame => frame.Decoded.Envelope.MessageKind == SnapshotKind);
        }

        public async Task HandshakeAsync()
        {
            await SendAsync(new ClientHelloMessage($"raw-{SessionId}"));
            await SendAsync(new ClientJoinRequestMessage(SessionId, $"Name {SessionId}"));
        }

        public Task WaitForJoinAcceptedAsync()
        {
            return WaitForAsync(frames => frames.Any(frame => frame.Decoded.Message is JoinAcceptedMessage), "join accepted");
        }

        public Task WaitForSnapshotTickAsync(ulong tick)
        {
            return WaitForAsync(
                frames => frames.Any(frame =>
                    frame.Decoded.Message is ServerSnapshotMessage snapshot && snapshot.Snapshot.ServerTick >= tick),
                $"snapshot tick {tick}");
        }

        public Task SendCommandAsync(bool walking)
        {
            _commandSequence++;
            return SendAsync(new PlayerCommandMessage(new PlayerCommandFrame(
                new PlayerSessionId(SessionId),
                _commandSequence,
                PlayerModeSwitchIntent.None,
                new PlayerMoveIntent(walking ? 1f : 0f, 0f),
                new PlayerLookIntent(0f, 0f, Vector3.UnitZ),
                PrimaryUsePressed: false)));
        }

        public Task SendActionAsync(IPlayerActionPayload payload)
        {
            _actionSequence++;
            return SendAsync(new PlayerActionMessage(new PlayerSessionId(SessionId), _actionSequence, payload));
        }

        public void PauseReading() => _readGate.Reset();

        public void ResumeReading() => _readGate.Set();

        // Applies every received message, in order, into a client world like PlayScene does.
        public ClientWorldReplay Replay(int floorSize)
        {
            using ClientWorldSession world = new(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            int verified = 0;
            int mismatches = 0;
            int mutations = 0;
            foreach (Frame frame in Frames)
            {
                switch (frame.Decoded.Message)
                {
                    case JoinAcceptedMessage accepted:
                        ClientJoinAcceptedApplier.ApplyToRuntime(world, accepted);
                        break;
                    case ServerSnapshotMessage snapshotMessage:
                        ServerSnapshotEnvelope snapshot = snapshotMessage.Snapshot;
                        RemoteWorldMutationApplier.ApplySnapshotToRuntime(world, snapshot);
                        mutations += snapshot.WorldMutations.Count;
                        if (snapshot.WorldChecksum.HasValue)
                        {
                            verified++;
                            mismatches += RemoteWorldMutationApplier.IsWorldOutOfSync(world, snapshot) ? 1 : 0;
                        }

                        break;
                }
            }

            return new ClientWorldReplay(verified, mismatches, WorldStateChecksum.Compute(world.Map), mutations);
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _readGate.Set();
            _tcp.Dispose();
            try
            {
                await _readLoop.WaitAsync(Timeout);
            }
            catch (Exception)
            {
            }

            _shutdown.Dispose();
        }

        private async Task SendAsync<TMessage>(TMessage message)
            where TMessage : IClientToServerMessage
        {
            _outboundSequence++;
            byte[] payload = _codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, _outboundSequence), message);
            await _stream.WriteAsync(_framer.Frame(payload));
        }

        private async Task WaitForAsync(Func<IReadOnlyList<Frame>, bool> condition, string what)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (!condition(Frames))
            {
                Assert.True(ReadFault == null, $"{SessionId} read loop faulted: {ReadFault}");
                Assert.True(DateTime.UtcNow < deadline, $"{SessionId} never received {what}.");
                await Task.Delay(5);
            }
        }

        private void ReadLoop()
        {
            byte[] chunk = new byte[16 * 1024];
            List<byte> pending = [];
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    _readGate.Wait(_shutdown.Token);
                    int read = _stream.Read(chunk, 0, chunk.Length);
                    if (read <= 0)
                    {
                        return;
                    }

                    pending.AddRange(chunk.AsSpan(0, read).ToArray());
                    byte[] buffered = pending.ToArray();
                    int offset = 0;
                    while (_framer.TryDeframe(buffered.AsSpan(offset), out byte[]? payload, out int consumed))
                    {
                        offset += consumed;
                        DecodedProtocolMessage decoded = _codec.Decode(payload);
                        (int payloadOffset, byte[] payloadJson) = SplitPayload(payload!);
                        lock (_frames)
                        {
                            _frames.Add(new Frame(payload!, decoded, payloadOffset, payloadJson));
                        }
                    }

                    pending.RemoveRange(0, offset);
                }
            }
            catch (Exception) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                ReadFault = ex;
            }
        }

        // Finds the raw bytes of the envelope's "payload" value.
        private static (int Offset, byte[] Json) SplitPayload(byte[] frame)
        {
            Utf8JsonReader reader = new(frame);
            reader.Read();
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isPayload = reader.ValueTextEquals("payload"u8);
                reader.Read();
                int start = (int)reader.TokenStartIndex;
                reader.Skip();
                if (isPayload)
                {
                    int end = (int)reader.BytesConsumed;
                    return (start, frame[start..end]);
                }
            }

            throw new InvalidOperationException("Frame has no payload.");
        }
    }
}
