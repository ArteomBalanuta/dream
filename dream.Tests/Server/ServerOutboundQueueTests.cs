using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Server;

// The tick loop used to await each connection's snapshot write in turn, so one client with a
// full socket buffer froze the world for everyone.
public class ServerOutboundQueueTests
{
    [Fact]
    public async Task StalledConnection_DoesNotBlockTicksOrOtherConnections()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        DedicatedServerHost host = CreateHost(new SequencedAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), codec, framer);
        RecordingConnection healthy = new("healthy");
        StalledConnection stalled = new("stalled");
        ServerConnection healthyConnection = new(healthy, host, codec, framer);
        ServerConnection stalledConnection = new(stalled, host, codec, framer);
        service.AttachConnection(healthyConnection);
        service.AttachConnection(stalledConnection);
        host.HandleClientMessage("healthy", new ClientJoinRequestMessage("session-a", "a"));
        host.HandleClientMessage("stalled", new ClientJoinRequestMessage("session-b", "b"));

        Task advance = Task.Run(async () =>
        {
            for (int index = 0; index < 5; index++)
            {
                await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
            }
        });

        Assert.True(advance.Wait(TimeSpan.FromSeconds(5)), "A stalled connection blocked the tick loop.");
        Assert.True(await healthyConnection.FlushOutboundAsync(TimeSpan.FromSeconds(5)));
        // Snapshots may merge when queued faster than they are written; every mutation still arrives in order.
        ServerSnapshotEnvelope[] received = healthy.Decode(codec, framer)
            .Select(message => Assert.IsType<ServerSnapshotMessage>(message).Snapshot)
            .ToArray();
        Assert.Equal(5ul, received[^1].ServerTick);
        Assert.Equal(
            [1ul, 2ul, 3ul, 4ul, 5ul],
            received.SelectMany(snapshot => snapshot.WorldMutations).Select(mutation => mutation.Sequence).ToArray());
        // The stalled connection's backlog stays bounded: at most one merged snapshot waits.
        Assert.InRange(stalledConnection.PendingOutboundCount, 0, 1);
        Assert.True(stalledConnection.CoalescedSnapshots >= 3);
        stalled.Release();
    }

    [Fact]
    public async Task CoalescedSnapshots_KeepEveryWorldMutationInOrder()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        StalledConnection transport = new("stalled");
        ServerConnection connection = new(transport, CreateHost(new SequencedAuthority()), codec, framer);

        connection.EnqueueSnapshot(new ServerSnapshotMessage(CreateSnapshot(1, mutationSequence: 1)));
        Assert.True(transport.FirstSendStarted.Wait(TimeSpan.FromSeconds(5)));
        connection.EnqueueSnapshot(new ServerSnapshotMessage(CreateSnapshot(2, mutationSequence: 2)));
        connection.EnqueueSnapshot(new ServerSnapshotMessage(CreateSnapshot(3, mutationSequence: 3)));
        transport.Release();
        Assert.True(await connection.FlushOutboundAsync(TimeSpan.FromSeconds(5)));

        ServerSnapshotEnvelope[] sent = transport.Decode(codec, framer)
            .Select(message => Assert.IsType<ServerSnapshotMessage>(message).Snapshot)
            .ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Equal(3ul, sent[1].ServerTick);
        Assert.Equal([2ul, 3ul], sent[1].WorldMutations.Select(mutation => mutation.Sequence).ToArray());
        Assert.Equal(3ul, sent[1].LastAcceptedCommandSequences["session-a"]);
    }

    [Fact]
    public async Task BacklogOverflow_FaultsConnectionAndServiceDropsIt()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        DedicatedServerHost host = CreateHost(new SequencedAuthority());
        await using DedicatedServerService service = new(host, new NoopServerTransport(), codec, framer);
        StalledConnection stalled = new("stalled");
        ServerConnection connection = new(stalled, host, codec, framer);
        service.AttachConnection(connection);

        // Action results are never merged, so a client that never reads eventually overflows.
        for (int index = 0; index <= ServerConnection.MaxPendingOutboundMessages + 1; index++)
        {
            connection.Enqueue(new ServerActionMessage(new AuthoritativeActionResult("session-b", (ulong)index, 1, true, null)));
        }

        Assert.True(connection.IsFaulted);
        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        Assert.Equal(0, service.ConnectionCount);
        stalled.Release();
    }

    [Fact]
    public void TryCoalesce_RefusesSnapshotsCarryingAFullWorld()
    {
        ServerSnapshotEnvelope withWorld = CreateSnapshot(1, mutationSequence: 1) with
        {
            World = new WorldSnapshot(10, 8, 10, [])
        };

        Assert.False(ServerSnapshotEnvelope.TryCoalesce(withWorld, CreateSnapshot(2, mutationSequence: 2), out _));
        Assert.False(ServerSnapshotEnvelope.TryCoalesce(CreateSnapshot(2, mutationSequence: 2), withWorld, out _));
    }

    private static ServerSnapshotEnvelope CreateSnapshot(ulong tick, ulong mutationSequence)
    {
        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong> { ["session-a"] = tick },
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            [new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(tick, 1f, 1f), Sequence: mutationSequence)],
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

    private sealed class SequencedAuthority : ITickDrivenAuthoritativeSessionHost
    {
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
            return CreateSnapshot(CurrentServerTick, CurrentServerTick);
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

    private class RecordingConnection : ITransportConnection
    {
        public RecordingConnection(string connectionId)
        {
            ConnectionId = connectionId;
        }

        public string ConnectionId { get; }

        public List<byte[]> Frames { get; } = [];

        public virtual ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            lock (Frames)
            {
                Frames.Add(payload.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(0);
        }

        public IReadOnlyList<IProtocolMessage> Decode(TransportJsonCodec codec, LengthPrefixedMessageFramer framer)
        {
            lock (Frames)
            {
                return Frames.Select(frame =>
                {
                    Assert.True(framer.TryDeframe(frame, out byte[]? payload, out _));
                    return codec.Decode(payload!).Message;
                }).ToArray();
            }
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

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
            await base.SendAsync(payload, cancellationToken);
        }
    }
}
