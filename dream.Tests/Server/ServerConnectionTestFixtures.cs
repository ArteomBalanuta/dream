using System.Threading.Channels;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Server;

// Shared fakes for the NETR-1 liveness and coalescing tests.
internal static class ServerConnectionTestFixtures
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    // For tests about something other than liveness whose fake peers never send, or that may run
    // longer than the idle timeout on a loaded machine.
    public static readonly ServerConnectionOptions WithoutLiveness = new()
    {
        IdleTimeout = TimeSpan.Zero,
        SendStallTimeout = TimeSpan.Zero
    };

    public static DedicatedServerHost CreateHost(ITickDrivenAuthoritativeSessionHost authority)
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(authority, router);
    }

    public static ServerSnapshotEnvelope CreateSnapshot(
        ulong tick,
        IReadOnlyDictionary<string, ulong> sequences,
        IReadOnlyList<WorldMutationDelta> mutations,
        IReadOnlyList<DamageEventDelta>? damage = null,
        ulong? checksum = null,
        PlayerInteractionMode mode = PlayerInteractionMode.Building)
    {
        return new ServerSnapshotEnvelope(
            tick,
            sequences,
            [new ActorSnapshot(1, CreatureKind.Player, "session-a", new Vector3(tick, 1f, 2f), Vector3.UnitZ, 100, 100, true)],
            mode,
            null,
            mutations,
            damage ?? Array.Empty<DamageEventDelta>(),
            checksum,
            checksum.HasValue ? tick : 0);
    }

    public static WorldMutationDelta Mutation(ulong sequence, WorldMutationKind kind = WorldMutationKind.CubePlaced)
    {
        return new WorldMutationDelta(kind, new Vector3(sequence % 7, 1f, sequence % 5), Sequence: sequence);
    }

    // One connection's frames, in order, decoded as its client sees them: delta actor frames are
    // rebuilt into full actors (SCALE-2), as RemoteClientSession does.
    public static IReadOnlyList<IProtocolMessage> Decode(IEnumerable<byte[]> frames)
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new(1 << 26);
        ActorStateCache actors = new();
        return frames.Select(frame =>
        {
            Assert.True(framer.TryDeframe(frame, out byte[]? payload, out _));
            IProtocolMessage message = codec.Decode(payload!).Message;
            return message is ServerSnapshotMessage snapshot ? actors.Apply(snapshot) : message;
        }).ToArray();
    }

    // A clock the test moves by hand.
    public sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = 1_000_000;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _timestamp, (long)(elapsed.TotalSeconds * TimestampFrequency));
        }
    }

    // Records sent frames. Sends wait on a gate the test opens and closes; inbound bytes are
    // whatever the test feeds. A closed gate ignores cancellation, like a socket write that never
    // completes: only DisposeAsync releases it.
    public sealed class GatedTransport : ITransportConnection
    {
        private readonly object _sync = new();
        private readonly List<byte[]> _frames = [];
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private TaskCompletionSource _gate = NewGate(open: true);
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedTransport(string connectionId, bool recordFrames = true)
        {
            ConnectionId = connectionId;
            RecordFrames = recordFrames;
        }

        public string ConnectionId { get; }

        public bool RecordFrames { get; }

        public SemaphoreSlim SendStarted { get; } = new(0, int.MaxValue);

        public bool IsDisposed => _disposed.Task.IsCompleted;

        public byte[][] Frames
        {
            get
            {
                lock (_sync)
                {
                    return _frames.ToArray();
                }
            }
        }

        public void Close()
        {
            lock (_sync)
            {
                if (_gate.Task.IsCompleted)
                {
                    _gate = NewGate(open: false);
                }
            }
        }

        public void Open()
        {
            lock (_sync)
            {
                _gate.TrySetResult();
            }
        }

        public void Feed(byte[] bytes) => _inbound.Writer.TryWrite(bytes);

        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Task gate;
            lock (_sync)
            {
                gate = _gate.Task;
            }

            SendStarted.Release();
            await Task.WhenAny(gate, _disposed.Task);
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (RecordFrames)
            {
                lock (_sync)
                {
                    _frames.Add(payload.ToArray());
                }
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
            _disposed.TrySetResult();
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private static TaskCompletionSource NewGate(bool open)
        {
            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (open)
            {
                gate.TrySetResult();
            }

            return gate;
        }
    }

    public sealed class NoopServerTransport : IServerTransport
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

    // Each tick exports a snapshot with MutationsPerTick sequenced mutations, and a full world on
    // a resync.
    public sealed class MutatingAuthority : ITickDrivenAuthoritativeSessionHost, IWorldResyncSource
    {
        private ulong _nextMutationSequence = 1;

        public int MutationsPerTick { get; init; } = 1;

        public int WorldResyncs { get; private set; }

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
            WorldResyncs++;
            return CreateSnapshot(CurrentServerTick, new Dictionary<string, ulong>(), Array.Empty<WorldMutationDelta>()) with
            {
                World = new WorldSnapshot(10, 8, 10, [])
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
