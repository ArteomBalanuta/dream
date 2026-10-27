using System.Buffers;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Server;

public class SharedSnapshotEncodingTests
{
    private readonly ITestOutputHelper _output;

    public SharedSnapshotEncodingTests(ITestOutputHelper output)
    {
        _output = output;
    }

#if DEBUG
    // Debug builds compile async methods to class state machines (232 B per AdvanceOnceAsync call).
    private const long AsyncStateMachineBudgetBytes = 256;
#else
    private const long AsyncStateMachineBudgetBytes = 0;
#endif

    // Per tick on top of the snapshot message: the SharedEncodedPayload, its Lazy and the Lazy's
    // factory delegate (184 B measured in Debug).
    private const long SharedPayloadBudgetBytes = 256;

    // ServerConnection reuses its queue nodes, so it allocates nothing per enqueue. The tick
    // thread is still charged roughly 40 B per connection that this test does not attribute to
    // any type of ours (it was there before WIRE-5 too, on top of the 48 B queue node); the
    // allowance keeps the assertion about the per-connection encode, not the runtime.
    private const long PerConnectionAllowanceBytes = 64;

    // Before WIRE-5 each connection serialized the snapshot itself (Encode once per connection per
    // tick) and its queue entry allocated a node: drained-queue ticks measured 496 B with 2
    // connections and 912-1040 B with 8 (Debug, diagnostics off).
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task Service_AdvanceOnce_EncodesPayloadOncePerTickWithFlatAllocation(int connectionCount)
    {
        CountingCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        DedicatedServerHost host = CreateHost(new SnapshotAuthority(rebuildEachTick: false));
        await using DedicatedServerService service = new(host, new NoopServerTransport(), codec, framer);
        ServerConnection[] connections = AttachJoined(service, host, codec, framer, connectionCount, _ => new NullTransportConnection());
        long snapshotMessageBytes = AllocationAssert.MeasureBytesPerIteration(() => host.CreateSnapshotMessage());

        // Back to back, the writers fall behind and some snapshots are merged on the tick thread.
        long backToBackBytes = AllocationAssert.MeasureBytesPerIteration(
            () => service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds).GetAwaiter().GetResult());
        Assert.True(await service.FlushOutboundAsync(TimeSpan.FromSeconds(5)));
        long drainedBytes = await MeasureTickWithDrainedQueuesAsync(service);

        const int countedTicks = 10;
        Assert.True(await service.FlushOutboundAsync(TimeSpan.FromSeconds(5)));
        codec.ResetCounts();
        for (int tick = 0; tick < countedTicks; tick++)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            Assert.True(await service.FlushOutboundAsync(TimeSpan.FromSeconds(5)));
        }

        _output.WriteLine(
            $"AdvanceOnceAsync with {connectionCount} joined connections: {drainedBytes} B per tick with drained queues, " +
            $"{backToBackBytes} B back to back, {snapshotMessageBytes} B of it the snapshot message " +
            $"(diagnostics {(NetworkDiagnosticsLog.IsEnabled ? "on" : "off")}); per tick: " +
            $"Encode {codec.EncodeCalls / (double)countedTicks:0.##}, EncodePayload {codec.EncodePayloadCalls / (double)countedTicks:0.##}, " +
            $"WriteEnvelope {codec.WriteEnvelopeCalls / (double)countedTicks:0.##}");
        Assert.All(connections, connection => Assert.False(connection.IsFaulted));
        Assert.Equal(countedTicks, codec.EncodePayloadCalls);
        Assert.Equal(countedTicks * connectionCount, codec.WriteEnvelopeCalls);
        Assert.Equal(0, codec.EncodeCalls);
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            long budget = snapshotMessageBytes + SharedPayloadBudgetBytes + AsyncStateMachineBudgetBytes +
                (connectionCount * PerConnectionAllowanceBytes);
            Assert.True(drainedBytes <= budget, $"Allocated {drainedBytes} B per tick; budget is {budget} B.");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public async Task Service_EncodesEachSnapshotPayloadOnce_RegardlessOfConnectionCount(int connectionCount)
    {
        CountingCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        DedicatedServerHost host = CreateHost(new SnapshotAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), codec, framer);
        AttachJoined(service, host, codec, framer, connectionCount, index => new RecordingConnection($"recording-{index}"));

        const int ticks = 4;
        for (int tick = 0; tick < ticks; tick++)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            Assert.True(await service.FlushOutboundAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(ticks, codec.EncodePayloadCalls);
        Assert.Equal(ticks * connectionCount, codec.WriteEnvelopeCalls);
        Assert.Equal(0, codec.EncodeCalls);
    }

    [Fact]
    public async Task ThreeConnections_ReceiveSharedPayloadInTheirOwnEnvelopes_MatchingThePerConnectionEncoding()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        SnapshotAuthority authority = new();
        DedicatedServerHost host = CreateHost(authority);
        await using DedicatedServerService service = new(host, new NoopServerTransport(), codec, framer);
        RecordingConnection[] transports = new RecordingConnection[3];
        AttachJoined(service, host, codec, framer, transports.Length, index => transports[index] = new RecordingConnection($"recording-{index}"));

        const int ticks = 4;
        for (int tick = 0; tick < ticks; tick++)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            Assert.True(await service.FlushOutboundAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(ticks, authority.Exported.Count);
        byte[][][] frames = transports.Select(transport => transport.SnapshotFrames()).ToArray();
        for (int tick = 0; tick < ticks; tick++)
        {
            ServerSnapshotMessage source = new(authority.Exported[tick]);
            byte[] sourcePayload = EncodePayload(codec, source);
            ulong sequence = (ulong)tick + 1;
            TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, source.MessageKind, sequence);
            byte[] perConnectionFrame = framer.Frame(codec.Encode(envelope, source));
            byte[]? firstPayloadSuffix = null;

            foreach (byte[][] connectionFrames in frames)
            {
                Assert.Equal(ticks, connectionFrames.Length);
                byte[] frame = connectionFrames[tick];

                // Byte-identical to the per-connection path the server used before WIRE-5.
                Assert.Equal(perConnectionFrame, frame);

                DecodedProtocolMessage decoded = DecodeFrame(codec, framer, frame);
                Assert.Equal(sequence, decoded.Envelope.Sequence);
                ServerSnapshotMessage received = Assert.IsType<ServerSnapshotMessage>(decoded.Message);
                Assert.Equal(source.Snapshot.ServerTick, received.Snapshot.ServerTick);
                Assert.Equal(source.Snapshot.LastAcceptedCommandSequences, received.Snapshot.LastAcceptedCommandSequences);
                Assert.Equal(source.Snapshot.Actors, received.Snapshot.Actors);
                Assert.Equal(source.Snapshot.WorldMutations, received.Snapshot.WorldMutations);
                Assert.Equal(sourcePayload, EncodePayload(codec, received));

                // Everything after the envelope prefix is the shared payload, then the closing brace.
                Assert.True(framer.TryReadFrameBounds(frame, out int payloadOffset, out int payloadLength, out _));
                ReadOnlySpan<byte> body = frame.AsSpan(payloadOffset, payloadLength);
                byte[] payloadSuffix = body[^(sourcePayload.Length + 1)..].ToArray();
                Assert.Equal(sourcePayload, payloadSuffix[..^1]);
                Assert.Equal((byte)'}', payloadSuffix[^1]);
                firstPayloadSuffix ??= payloadSuffix;
                Assert.Equal(firstPayloadSuffix, payloadSuffix);
            }
        }
    }

    [Fact]
    public async Task MergedSnapshot_IsEncodedPerConnection_AndItsSharedPayloadsAreNeverEncoded()
    {
        CountingCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        StalledConnection transport = new("stalled");
        await using ServerConnection connection = new(transport, CreateHost(new SnapshotAuthority()), codec, framer);
        ServerSnapshotMessage first = new(CreateSnapshot(1, 2));
        ServerSnapshotMessage second = new(CreateSnapshot(2, 2));
        ServerSnapshotMessage third = new(CreateSnapshot(3, 2));
        SharedEncodedPayload firstShared = new(codec, first);
        SharedEncodedPayload secondShared = new(codec, second);
        SharedEncodedPayload thirdShared = new(codec, third);

        connection.EnqueueSnapshot(first, firstShared);
        Assert.True(transport.FirstSendStarted.Wait(TimeSpan.FromSeconds(5)));
        connection.EnqueueSnapshot(second, secondShared);
        connection.EnqueueSnapshot(third, thirdShared);
        Assert.Equal(1, connection.CoalescedSnapshots);
        transport.Release();
        Assert.True(await connection.FlushOutboundAsync(TimeSpan.FromSeconds(5)));

        Assert.True(firstShared.IsEncoded);
        Assert.False(secondShared.IsEncoded);
        Assert.False(thirdShared.IsEncoded);
        Assert.Equal(1, codec.EncodePayloadCalls);
        Assert.Equal(1, codec.WriteEnvelopeCalls);
        Assert.Equal(1, codec.EncodeCalls);

        DecodedProtocolMessage[] sent = transport.SnapshotFrames().Select(frame => DecodeFrame(codec, framer, frame)).ToArray();
        Assert.Equal([1ul, 2ul], sent.Select(message => message.Envelope.Sequence).ToArray());
        ServerSnapshotEnvelope merged = Assert.IsType<ServerSnapshotMessage>(sent[1].Message).Snapshot;
        Assert.Equal(3ul, merged.ServerTick);
        Assert.Equal([2ul, 3ul], merged.WorldMutations.Select(mutation => mutation.Sequence).ToArray());
    }

    [Fact]
    public async Task FailedSharedEncode_RunsOnce_AndFaultsEveryConnection()
    {
        CountingCodec codec = new() { FailEncodePayload = true };
        LengthPrefixedMessageFramer framer = new();
        DedicatedServerHost host = CreateHost(new SnapshotAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), codec, framer);
        ServerConnection[] connections = AttachJoined(service, host, codec, framer, 3, index => new RecordingConnection($"recording-{index}"));

        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        Assert.False(await service.FlushOutboundAsync(TimeSpan.FromSeconds(5)));

        Assert.All(connections, connection => Assert.True(connection.IsFaulted));
        Assert.Equal(1, codec.EncodePayloadCalls);
        Assert.Equal(0, codec.WriteEnvelopeCalls);
    }

    [Fact]
    public async Task EnqueueSnapshot_RejectsAPayloadSharedFromAnotherMessage()
    {
        TransportJsonCodec codec = new();
        await using ServerConnection connection = new(
            new RecordingConnection("recording"),
            CreateHost(new SnapshotAuthority()),
            codec,
            new LengthPrefixedMessageFramer());
        ServerSnapshotMessage snapshot = new(CreateSnapshot(1, 1));
        SharedEncodedPayload other = new(codec, new ServerSnapshotMessage(CreateSnapshot(1, 1)));

        Assert.Throws<ArgumentException>(() => connection.EnqueueSnapshot(snapshot, other));
        Assert.Equal(0, connection.PendingOutboundCount);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndLaterSendsThrowObjectDisposed()
    {
        RecordingConnection transport = new("recording");
        ServerConnection connection = new(
            transport,
            CreateHost(new SnapshotAuthority()),
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer());
        await connection.SendSnapshotAsync(new ServerSnapshotMessage(CreateSnapshot(1, 1)));

        await connection.DisposeAsync();
        await connection.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await connection.SendActionAsync(new ServerActionMessage(new AuthoritativeActionResult("session-0", 1, 1, true, null))));
        connection.Enqueue(new ServerActionMessage(new AuthoritativeActionResult("session-0", 2, 1, true, null)));
        Assert.Equal(0, connection.PendingOutboundCount);
        Assert.Single(transport.SnapshotFrames());
    }

    [Fact]
    public async Task PublicSendsRacingTheWriterTask_ProduceStrictlyIncreasingGapFreeSequences()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        YieldingRecordingConnection transport = new("racing");
        await using ServerConnection connection = new(transport, CreateHost(new SnapshotAuthority()), codec, framer);

        const int senders = 6;
        const int messagesPerSender = 150;
        const int queuedMessages = 300;
        // The barrier blocks its participants, so each starts on a dedicated thread rather than
        // holding thread-pool threads that other tests running in parallel need.
        using Barrier start = new(senders + 1);
        Task[] tasks = new Task[senders + 1];
        for (int sender = 0; sender < senders; sender++)
        {
            int senderIndex = sender;
            tasks[sender] = StartOnDedicatedThread(async () =>
            {
                start.SignalAndWait();
                for (int index = 0; index < messagesPerSender; index++)
                {
                    if ((senderIndex + index) % 2 == 0)
                    {
                        await connection.SendSnapshotAsync(new ServerSnapshotMessage(CreateSnapshot((ulong)index, 1)));
                    }
                    else
                    {
                        await connection.SendActionAsync(new ServerActionMessage(
                            new AuthoritativeActionResult("session-0", (ulong)index, 1, true, null)));
                    }
                }
            });
        }

        tasks[senders] = StartOnDedicatedThread(() =>
        {
            start.SignalAndWait();
            for (int index = 0; index < queuedMessages; index++)
            {
                // Actions are never merged, so every queued message reaches the wire through the writer task.
                connection.Enqueue(new ServerActionMessage(new AuthoritativeActionResult("session-1", (ulong)index, 1, true, null)));
            }

            return Task.CompletedTask;
        });

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(await connection.FlushOutboundAsync(TimeSpan.FromSeconds(10)));
        Assert.False(connection.IsFaulted);

        ulong[] sequences = transport.SnapshotFrames()
            .Select(frame => DecodeFrame(codec, framer, frame).Envelope.Sequence)
            .ToArray();
        int expectedCount = (senders * messagesPerSender) + queuedMessages;
        Assert.Equal(expectedCount, sequences.Length);
        for (int index = 0; index < sequences.Length; index++)
        {
            Assert.True(
                sequences[index] == (ulong)index + 1,
                $"Frame {index} carried sequence {sequences[index]}; expected {index + 1}.");
        }
    }

    // One tick at a time, each started after every writer has drained its queue, so no snapshot
    // is merged; the smallest tick is the steady-state cost of the tick path itself.
    private static async Task<long> MeasureTickWithDrainedQueuesAsync(DedicatedServerService service)
    {
        long best = long.MaxValue;
        for (int round = 0; round < 200; round++)
        {
            Assert.True(await service.FlushOutboundAsync(TimeSpan.FromSeconds(5)));
            long before = GC.GetAllocatedBytesForCurrentThread();
            service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds).GetAwaiter().GetResult();
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return best;
    }

    private static ServerConnection[] AttachJoined(
        DedicatedServerService service,
        DedicatedServerHost host,
        IProtocolCodec codec,
        IMessageFramer framer,
        int count,
        Func<int, ITransportConnection> createTransport)
    {
        ServerConnection[] connections = new ServerConnection[count];
        for (int index = 0; index < count; index++)
        {
            ITransportConnection transport = createTransport(index);
            connections[index] = new ServerConnection(transport, host, codec, framer);
            service.AttachConnection(connections[index]);
            host.HandleClientMessage(transport.ConnectionId, new ClientJoinRequestMessage($"session-{index}", $"player-{index}"));
        }

        return connections;
    }

    private static Task StartOnDedicatedThread(Func<Task> action)
    {
        return Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private static byte[] EncodePayload(IProtocolCodec codec, ServerSnapshotMessage message)
    {
        ArrayBufferWriter<byte> writer = new();
        codec.EncodePayload(message, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static DecodedProtocolMessage DecodeFrame(IProtocolCodec codec, IMessageFramer framer, byte[] frame)
    {
        Assert.True(framer.TryReadFrameBounds(frame, out int payloadOffset, out int payloadLength, out int consumed));
        Assert.Equal(frame.Length, consumed);
        return codec.Decode(frame.AsSpan(payloadOffset, payloadLength));
    }

    private static ServerSnapshotEnvelope CreateSnapshot(ulong tick, int sessionCount)
    {
        Dictionary<string, ulong> sequences = new(StringComparer.Ordinal);
        ActorSnapshot[] actors = new ActorSnapshot[sessionCount];
        for (int index = 0; index < sessionCount; index++)
        {
            string sessionId = $"session-{index}";
            sequences[sessionId] = tick + (ulong)index;
            actors[index] = new ActorSnapshot(
                100 + index,
                CreatureKind.Player,
                sessionId,
                new Vector3(index, 2f, tick),
                -Vector3.UnitZ,
                90,
                100,
                true,
                PlayerInteractionMode.Combat,
                DisplayName: $"player-{index}");
        }

        return new ServerSnapshotEnvelope(
            tick,
            sequences,
            actors,
            PlayerInteractionMode.Building,
            null,
            [new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(tick, 1f, 1f), Sequence: tick)],
            Array.Empty<DamageEventDelta>());
    }

    private static DedicatedServerHost CreateHost(ITickDrivenAuthoritativeSessionHost authority)
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(authority, router);
    }

    // Exports a snapshot whose accepted command sequences cover every joined session. With
    // rebuildEachTick false the snapshot is built only on join, so the authority allocates nothing
    // per tick and allocation measurements show only the service and its connections.
    private sealed class SnapshotAuthority : ITickDrivenAuthoritativeSessionHost
    {
        private readonly bool _rebuildEachTick;
        private readonly List<string> _sessions = [];
        private ServerSnapshotEnvelope _current = CreateSnapshot(0, 0);

        public SnapshotAuthority(bool rebuildEachTick = true)
        {
            _rebuildEachTick = rebuildEachTick;
        }

        public ulong CurrentServerTick { get; private set; }

        // Every snapshot handed to the host, in order (only recorded while rebuilding each tick).
        public List<ServerSnapshotEnvelope> Exported { get; } = [];

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            _sessions.Add(requestedSessionId);
            _current = CreateSnapshot(CurrentServerTick, _sessions.Count);
            return new JoinAcceptedMessage(requestedSessionId, 101, CurrentServerTick, InitialWorld: null);
        }

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
        {
            return new AuthoritativeActionResult(message.SessionId.Value, message.ActionSequence, CurrentServerTick, true, null);
        }

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false)
        {
            if (_rebuildEachTick)
            {
                Exported.Add(_current);
            }

            return _current;
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
            if (_rebuildEachTick)
            {
                _current = CreateSnapshot(serverTick, _sessions.Count);
            }

            return Array.Empty<AuthoritativeActionResult>();
        }
    }

    // Counts calls into the real codec; the counters are read after the writers have flushed.
    private sealed class CountingCodec : IProtocolCodec
    {
        private readonly TransportJsonCodec _inner = new();
        private int _encodeCalls;
        private int _encodePayloadCalls;
        private int _writeEnvelopeCalls;

        public int EncodeCalls => Volatile.Read(ref _encodeCalls);

        public int EncodePayloadCalls => Volatile.Read(ref _encodePayloadCalls);

        public int WriteEnvelopeCalls => Volatile.Read(ref _writeEnvelopeCalls);

        public bool FailEncodePayload { get; init; }

        public void ResetCounts()
        {
            Interlocked.Exchange(ref _encodeCalls, 0);
            Interlocked.Exchange(ref _encodePayloadCalls, 0);
            Interlocked.Exchange(ref _writeEnvelopeCalls, 0);
        }

        public byte[] Encode<TMessage>(TransportEnvelope envelope, TMessage message)
            where TMessage : IProtocolMessage
        {
            Interlocked.Increment(ref _encodeCalls);
            return _inner.Encode(envelope, message);
        }

        public void Encode<TMessage>(TransportEnvelope envelope, TMessage message, IBufferWriter<byte> destination)
            where TMessage : IProtocolMessage
        {
            Interlocked.Increment(ref _encodeCalls);
            _inner.Encode(envelope, message, destination);
        }

        public void EncodePayload<TMessage>(TMessage message, IBufferWriter<byte> destination)
            where TMessage : IProtocolMessage
        {
            Interlocked.Increment(ref _encodePayloadCalls);
            if (FailEncodePayload)
            {
                throw new InvalidOperationException("Simulated payload encode failure.");
            }

            _inner.EncodePayload(message, destination);
        }

        public void WriteEnvelope(TransportEnvelope envelope, ReadOnlySpan<byte> payloadJson, IBufferWriter<byte> destination)
        {
            Interlocked.Increment(ref _writeEnvelopeCalls);
            _inner.WriteEnvelope(envelope, payloadJson, destination);
        }

        public DecodedProtocolMessage Decode(ReadOnlySpan<byte> payload)
        {
            return _inner.Decode(payload);
        }
    }

    private sealed class NoopServerTransport : IServerTransport
    {
        public int Port => 5057;

        public void Start()
        {
        }

        public ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<ITransportConnection>(new InvalidOperationException("not used"));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullTransportConnection : ITransportConnection
    {
        private static int s_nextId;

        public string ConnectionId { get; } = $"null-{Interlocked.Increment(ref s_nextId)}";

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(0);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private class RecordingConnection : ITransportConnection
    {
        private readonly List<byte[]> _frames = [];

        public RecordingConnection(string connectionId)
        {
            ConnectionId = connectionId;
        }

        public string ConnectionId { get; }

        public virtual ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Record(payload);
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(0);
        }

        public byte[][] SnapshotFrames()
        {
            lock (_frames)
            {
                return _frames.ToArray();
            }
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        protected void Record(ReadOnlyMemory<byte> payload)
        {
            lock (_frames)
            {
                _frames.Add(payload.ToArray());
            }
        }
    }

    // Holds every send until Release, so later snapshots queue up behind the first and merge.
    private sealed class StalledConnection : RecordingConnection
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StalledConnection(string connectionId)
            : base(connectionId)
        {
        }

        public ManualResetEventSlim FirstSendStarted { get; } = new();

        public void Release()
        {
            _gate.TrySetResult();
        }

        public override async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            FirstSendStarted.Set();
            await _gate.Task.WaitAsync(cancellationToken);
            Record(payload);
        }
    }

    // Records the frame, then yields, so concurrent senders overlap around the send.
    private sealed class YieldingRecordingConnection : RecordingConnection
    {
        public YieldingRecordingConnection(string connectionId)
            : base(connectionId)
        {
        }

        public override async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Record(payload);
            await Task.Yield();
        }
    }
}
