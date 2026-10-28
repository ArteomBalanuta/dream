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
using Dream.Tests.Utilities;

namespace Dream.Tests.Integration;

// WIRE-5, WIRE-6 and NET-6 together over real loopback TCP: the real DedicatedServerService and
// a runtime-backed host, ticked by the test one fixed step at a time, with raw socket clients
// that keep every frame's bytes. The clients speak protocol v2 by hand, so the test sees exactly
// what the server puts on the wire: one snapshot payload serialized once and wrapped in each
// connection's own envelope, slimmed to v2, with actors in canonical order. Every client also
// applies what it receives into a ClientWorldSession, like PlayScene, and checks the world
// checksums the server sends.
//
// The slow-client and v1-rejection cases live in ProtocolV2SlowClientLoopbackIntegrationTests and
// ProtocolV2VersionRejectionLoopbackIntegrationTests so the three runs go in parallel; they use
// the raw client and server harness below.
public class ProtocolV2SharedSnapshotLoopbackIntegrationTests(ITestOutputHelper output)
{
    internal const string SnapshotKind = "server.snapshot";
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Join order is deliberately not ordinal: the primary seat goes to the first joiner, and the
    // two secondaries join in reverse ordinal order.
    internal const string PrimarySession = "session-m";
    internal const string LateSecondary = "session-b";
    internal const string EarlySecondary = "session-z";

    // Cells within the primary's build reach of its spawn at (5, 1.5, 5).
    internal static readonly Vector3[] BuildCells =
    [
        new(7f, 1f, 5f), new(3f, 1f, 5f), new(5f, 1f, 7f), new(5f, 1f, 3f),
        new(7f, 1f, 6f), new(3f, 1f, 4f), new(6f, 1f, 7f), new(4f, 1f, 3f)
    ];

    // Build use is server-originated only (NETR-6 rejects it over player.action), so the primary's
    // builds are queued on the host; the placed cubes still reach every client through snapshots.
    internal static void EnqueuePrimaryBuild(ServerUnderTest server, int buildIndex)
    {
        Assert.True(server.Host.TryEnqueueServerAction(new PlayerActionMessage(
            new PlayerSessionId(PrimarySession),
            (ulong)(buildIndex + 1),
            new BuildUseIntent(BuildCells[buildIndex]))));
    }

    [Fact]
    public async Task ThreeClients_ReceiveEverySnapshotOnce_SharedPayloadBytes_GapFreeEnvelopes_AndMatchingWorlds()
    {
        await using ServerUnderTest server = await ServerUnderTest.StartAsync();
        await using RawClient primary = await server.ConnectAndJoinAsync(PrimarySession);
        await using RawClient early = await server.ConnectAndJoinAsync(EarlySecondary);
        await using RawClient late = await server.ConnectAndJoinAsync(LateSecondary);
        RawClient[] clients = [primary, early, late];

        // 90 ticks: every build is placed by tick 75, the walkers start and stop at least once,
        // and the runtime's world checksum (every 64 ticks) reaches the clients at least twice,
        // once after several builds have landed.
        const int Ticks = 90;
        int builds = 0;
        for (int i = 0; i < Ticks; i++)
        {
            // The primary stays within build reach of its spawn; the secondaries walk.
            await primary.SendCommandAsync(walking: false);
            await early.SendCommandAsync(walking: i % 60 < 20);
            await late.SendCommandAsync(walking: i % 50 < 10);

            if (i % 10 == 5 && builds < BuildCells.Length)
            {
                EnqueuePrimaryBuild(server, builds++);
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
        // SCALE-2: the one exception is a connection's first snapshot after its join, which goes
        // out as the payload's keyframe twin (the connection holds no delta base yet); its actors
        // still match.
        int differingEnvelopes = 0;
        int keyframeTwins = 0;
        foreach (ulong tick in sharedTicks)
        {
            Frame reference = snapshotsByTick[0][tick];
            for (int c = 1; c < clients.Length; c++)
            {
                Frame other = snapshotsByTick[c][tick];
                differingEnvelopes += reference.Decoded.Envelope.Sequence != other.Decoded.Envelope.Sequence ? 1 : 0;
                if (reference.PayloadJson.AsSpan().SequenceEqual(other.PayloadJson))
                {
                    continue;
                }

                ServerSnapshotEnvelope referenceSnapshot = ((ServerSnapshotMessage)reference.Decoded.Message).Snapshot;
                ServerSnapshotEnvelope otherSnapshot = ((ServerSnapshotMessage)other.Decoded.Message).Snapshot;
                Assert.True(
                    referenceSnapshot.ActorFrame?.IsKeyframe != otherSnapshot.ActorFrame?.IsKeyframe,
                    $"tick {tick}: {clients[c].SessionId}'s payload differs from {clients[0].SessionId}'s.");
                Assert.Equal(referenceSnapshot.ActorFrame!.Sequence, otherSnapshot.ActorFrame!.Sequence);
                Assert.Equal(referenceSnapshot.Actors, otherSnapshot.Actors);
                keyframeTwins++;
            }
        }

        Assert.True(keyframeTwins <= clients.Length - 1, $"{keyframeTwins} keyframe twins; at most one per later join expected.");

        // The joins happened at different points, so the per-connection sequences really differ.
        Assert.True(differingEnvelopes > 0, "Every connection used the same envelope sequences.");

        // The world changed, and every client rebuilt the server's world from the stream.
        Assert.True(primary.AcceptedActions > 0, "No build was accepted.");
        ulong serverChecksum = WorldStateChecksum.Compute(server.Host.Runtime!.Map);
        // Each replay builds its own client world from its own client's frames (no frame arrives
        // any more), so the three run at once.
        int floorSize = server.Host.Runtime!.Map.Width;
        ClientWorldReplay[] replays = await Task.WhenAll(clients.Select(client => Task.Run(() => client.Replay(floorSize))));
        for (int c = 0; c < clients.Length; c++)
        {
            RawClient client = clients[c];
            ClientWorldReplay replay = replays[c];
            Assert.True(replay.ChecksumsVerified >= 2, $"{client.SessionId} verified {replay.ChecksumsVerified} checksums.");
            Assert.Equal(0, replay.ChecksumMismatches);
            Assert.Equal(serverChecksum, replay.FinalChecksum);
            Assert.True(replay.MutationsApplied >= primary.AcceptedActions, $"{client.SessionId} applied {replay.MutationsApplied} mutations.");
        }

        output.WriteLine(
            $"ticks={lastTick} sharedTicks={sharedTicks.Length} builds={primary.AcceptedActions} " +
            $"frames=[{string.Join(",", clients.Select(client => client.Frames.Count))}]");
    }

    internal static void AssertEnvelopeSequencesAreGapFree(RawClient client)
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

    internal static void AssertSlimV2Payloads(RawClient client)
    {
        foreach (Frame frame in client.Frames)
        {
            string payload = Encoding.UTF8.GetString(frame.PayloadJson);
            Assert.DoesNotContain("messageKind", payload, StringComparison.Ordinal);
            Assert.DoesNotContain(":null", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("effectiveRoster", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("effectiveInitialActors", payload, StringComparison.Ordinal);
            // SCALE-2: a shared snapshot payload has the connection's own accepted command sequence
            // in the envelope header, and no sequence map of its own.
            string ack = frame.Decoded.Envelope.Ack is TransportAck header
                ? (header.SessionId is string session ? $"\"ack\":{{\"{session}\":{header.Sequence}}}," : "\"ack\":{},")
                : string.Empty;
            if (frame.Decoded.Message is ServerSnapshotMessage { SharedBody: true })
            {
                Assert.NotNull(frame.Decoded.Envelope.Ack);
                Assert.DoesNotContain("lastAcceptedCommandSequences", payload, StringComparison.Ordinal);
            }

            string expectedPrefix =
                $"{{\"protocolVersion\":4,\"messageKind\":\"{frame.Decoded.Envelope.MessageKind}\",\"sequence\":{frame.Decoded.Envelope.Sequence},{ack}\"payload\":";
            Assert.Equal(expectedPrefix, Encoding.UTF8.GetString(frame.Payload, 0, frame.PayloadOffset));
            Assert.Equal((byte)'}', frame.Payload[^1]);
            Assert.Equal(frame.Payload.Length - 1, frame.PayloadOffset + frame.PayloadJson.Length);
        }
    }

    // NET-6: secondaries are captured in ordinal session-id order whatever the join order. The
    // primary seat still comes first (it is whoever joined first), a documented gap.
    internal static void AssertCanonicalActorOrder(RawClient client)
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

    internal static Dictionary<ulong, Frame> SnapshotsByTick(RawClient client)
    {
        return client.Snapshots().ToDictionary(frame => ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot.ServerTick);
    }

    internal static ulong TickOf(Frame frame)
    {
        return ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot.ServerTick;
    }

    internal static string[] MutationStream(RawClient client, ulong after)
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

    internal static string[] DamageStream(RawClient client, ulong after)
    {
        return client.Snapshots()
            .Select(frame => ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot)
            .Where(snapshot => snapshot.ServerTick > after)
            .SelectMany(snapshot => snapshot.DamageEvents)
            .Select(damage => damage.ToString())
            .ToArray();
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, string failure)
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
    internal sealed class ServerUnderTest : IAsyncDisposable
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

        public static Task<ServerUnderTest> StartAsync(ServerConnectionOptions? connectionOptions = null)
        {
            DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
            DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                // Unless a test passes its own options, the server sends a snapshot every tick: the
                // tests step it one tick at a time and expect that tick's snapshot.
                connectionOptions ?? PerTickSnapshots.Options);
            service.Start();
            return Task.FromResult(new ServerUnderTest(host, service));
        }

        public ValueTask<ServerConnection> AcceptAsync()
        {
            return new ValueTask<ServerConnection>(Service.AcceptOnThreadPoolAsync());
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
                flushed &= await OutboundDrain.FlushAsync(connection, Timeout);
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
        // Rebuilds full actors from delta frames in wire order, as RemoteClientSession does.
        private readonly ActorStateCache _actorStates = new();
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
            // Protocol v3: join and resync worlds arrive in chunks, reassembled as the client does.
            foreach (IServerToClientMessage message in WorldTransferReassembler.Reassemble(Frames.Select(frame => frame.Decoded.Message)))
            {
                switch (message)
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
                        if (decoded.Message is ServerSnapshotMessage snapshot)
                        {
                            decoded = decoded with { Message = _actorStates.Apply(snapshot) };
                        }

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
