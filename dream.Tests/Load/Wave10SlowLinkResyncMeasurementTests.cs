using System.Diagnostics;
using System.Threading.Channels;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Multiplayer.Protocol;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Load;

// Wave 10 tester, SCALE-1 x NETR-2 watchdog x NETR-1 idle (DREAM_LOAD_TEST=1): a 1.1 MB World50k
// resync to a joined client over a link that delivers about 50 KB/s, server to client, in 2 KB
// slices (no kernel buffer: a server write completes when its last byte has arrived). The client
// session holds every message behind the carrier until the final chunk, so remote actors freeze for
// the whole transfer. Checked with the default liveness options on the server and the real
// RemoteClientSession (keepalive, 8 s silence watchdog):
//  - neither end drops the session during a ~22 s transfer;
//  - remote-actor presentation (ServerTickClock + TickInterpolationBuffer, as PlayScene drives
//    them) freezes for about the transfer time and recovers; any backward step of the rendered
//    remote actor after the transfer is measured and reported.
[Collection(TimingSensitiveCollection.Name)]
public sealed class Wave10SlowLinkResyncMeasurementTests(ITestOutputHelper output)
{
    private const int LinkBytesPerSecond = 50 * 1024;
    private const int SnapshotIntervalTicks = 2;

    [LoadFact]
    public async Task World50kResync_At50KBps_KeepsTheSessionAlive_AndRemoteActorsRecover()
    {
        // 50k cubes encode to only ~640 KB in the v3 compact format; 85k cubes make the ~1.1 MB resync.
        WorldSnapshot world = WorldTransferProtocolTests.CreateWorld(cubes: 85_000, rubble: 1_000, subcells: 10);
        ThrottledLink link = new("slow-50k", LinkBytesPerSecond);
        using DedicatedServerHost host = CreateHost(new MutatingAuthority { MutationsPerTick = 0 });
        await using ServerConnection connection = new(link.Server, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), ServerConnectionOptions.Default);
        await using RemoteClientSession client = new(link.Client, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), new RemoteClientOptions("127.0.0.1", 1, "session-a", "Slow A", "w10-slow"));
        client.StartBackgroundReceive();
        using CancellationTokenSource stop = new();
        Task serverReceive = Task.Run(async () =>
        {
            try
            {
                while (await connection.ProcessIncomingAsync(stop.Token))
                {
                }
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
            }
        });

        Stopwatch clock = Stopwatch.StartNew();
        connection.Enqueue(new ServerHelloMessage("w10-slow", TransportEnvelope.CurrentProtocolVersion, 0));
        connection.Enqueue(new JoinAcceptedMessage("session-a", 1, CurrentTick(clock), InitialWorld: null));
        string? serverFault = null;
        bool resyncQueued = false;
        double resyncQueuedAt = 0;
        Task serverTicks = Task.Run(async () =>
        {
            ulong lastSent = 0;
            while (!stop.IsCancellationRequested)
            {
                ulong tick = CurrentTick(clock);
                if (tick >= lastSent + SnapshotIntervalTicks)
                {
                    lastSent = tick;
                    if (!resyncQueued && clock.Elapsed.TotalSeconds >= 2)
                    {
                        resyncQueued = true;
                        resyncQueuedAt = clock.Elapsed.TotalSeconds;
                        connection.Enqueue(new ServerSnapshotMessage(CreateSnapshot(tick, new Dictionary<string, ulong>(), []) with { World = world }));
                    }
                    else
                    {
                        connection.EnqueueSnapshot(new ServerSnapshotMessage(CreateSnapshot(tick, new Dictionary<string, ulong>(), [])));
                    }
                }

                if (connection.CheckLiveness() && serverFault == null)
                {
                    serverFault = connection.CloseReason.ToString();
                }

                await Task.Delay(5);
            }
        });

        // The client's presentation loop, as PlayScene drives it, at ~60 Hz.
        ServerTickClock tickClock = new();
        TickInterpolationBuffer remoteActor = new();
        List<(double Time, double RenderTick, float X, ulong Newest)> frames = [];
        double transferStart = -1;
        double transferEnd = -1;
        double lastFrame = clock.Elapsed.TotalSeconds;
        bool worldDelivered = false;
        int heldDelivered = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(16);
            double now = clock.Elapsed.TotalSeconds;
            tickClock.Advance(now - lastFrame);
            lastFrame = now;
            bool receiving = client.IsReceivingWorld;
            if (receiving && transferStart < 0)
            {
                transferStart = now;
            }

            while (client.TryDequeueInbound(out IServerToClientMessage? message))
            {
                switch (message)
                {
                    case JoinAcceptedMessage accepted:
                        tickClock.Reset(accepted.CurrentServerTick);
                        break;
                    case ServerSnapshotMessage snapshot:
                        if (snapshot.Snapshot.World != null)
                        {
                            worldDelivered = true;
                            Assert.Equal(world.PlacedCubes!.Count, snapshot.Snapshot.World.Value.PlacedCubes!.Count);
                        }
                        else if (worldDelivered && transferEnd < 0)
                        {
                            heldDelivered++;
                        }

                        tickClock.Observe(snapshot.Snapshot.ServerTick);
                        foreach (ActorSnapshot actor in snapshot.Snapshot.Actors)
                        {
                            remoteActor.AddSample(snapshot.Snapshot.ServerTick, actor.Position, actor.Facing);
                        }

                        break;
                }
            }

            if (transferStart >= 0 && transferEnd < 0 && worldDelivered)
            {
                transferEnd = now;
            }

            if (remoteActor.TrySample(tickClock.RenderTick, out InterpolatedActorPose pose))
            {
                frames.Add((now, tickClock.RenderTick, pose.Position.X, remoteActor.NewestTick ?? 0));
            }

            Assert.True(client.IsConnected, $"client dropped at {now:0.0} s: {client.DisconnectReason}");
            Assert.True(serverFault == null, $"server faulted the connection at {now:0.0} s: {serverFault}");
            if (transferEnd > 0 && now - transferEnd > 5)
            {
                break;
            }
        }

        stop.Cancel();
        await serverTicks;
        await link.DisposeAsync();
        await serverReceive;

        Assert.True(worldDelivered, $"the resync never completed (receiving since {transferStart:0.0} s, {connection.WorldChunksSent} chunks sent)");
        double transferSeconds = transferEnd - transferStart;

        // Freeze: the longest run of frames whose rendered remote X did not move.
        double longestFreeze = 0;
        double freezeStart = frames[0].Time;
        for (int i = 1; i < frames.Count; i++)
        {
            if (frames[i].X != frames[i - 1].X)
            {
                freezeStart = frames[i].Time;
            }
            else
            {
                longestFreeze = Math.Max(longestFreeze, frames[i].Time - freezeStart);
            }
        }

        // Backward steps of the rendered remote actor (X == server tick, so it should never fall).
        var afterTransfer = frames.Where(frame => frame.Time >= transferEnd - 0.05).ToList();
        double worstBackwardTicks = 0;
        double worstBackwardAt = 0;
        int backwardFrames = 0;
        for (int i = 1; i < frames.Count; i++)
        {
            double back = frames[i - 1].X - frames[i].X;
            if (back > 0.001)
            {
                backwardFrames++;
                if (back > worstBackwardTicks)
                {
                    worstBackwardTicks = back;
                    worstBackwardAt = frames[i].Time;
                }
            }
        }

        double renderLagAfter = afterTransfer.Count == 0 ? double.NaN : CurrentTick(clock) - afterTransfer[^1].X;
        output.WriteLine(
            $"world {connection.WorldChunksSent} chunks, {connection.BytesSent:N0} B to the client in {connection.FramesSent} frames " +
            $"({connection.BytesSent / clock.Elapsed.TotalSeconds / 1024:0.0} KB/s average over the run); remote actor {renderLagAfter:0.0} ticks behind the server at the end");
        output.WriteLine($"resync queued at {resyncQueuedAt:0.00} s; client receiving {transferStart:0.00} s -> {transferEnd:0.00} s ({transferSeconds:0.0} s), {heldDelivered} held snapshots delivered with it");
        output.WriteLine($"longest remote-actor freeze {longestFreeze:0.00} s; backward steps: {backwardFrames} frame(s), worst {worstBackwardTicks:0.0} ticks ({worstBackwardTicks / SimulationClock.AuthoritativeTicksPerSecond * 1000:0} ms) at {worstBackwardAt:0.00} s");
        output.WriteLine($"session alive: client {client.IsConnected}, server faulted {connection.IsFaulted}; client inbound frames {client.InboundFrameCount}");
        foreach (var frame in frames.Where(frame => frame.Time >= transferEnd - 0.1 && frame.Time <= transferEnd + 0.5))
        {
            output.WriteLine($"  t={frame.Time:0.000} renderTick={frame.RenderTick:0.0} x={frame.X:0.0} newest={frame.Newest}");
        }

        Assert.InRange(transferSeconds, 15, 45);
        Assert.InRange(longestFreeze, transferSeconds * 0.7, transferSeconds + 3);
        Assert.True(worstBackwardTicks < 1, $"the remote actor stepped back {worstBackwardTicks:0.0} ticks after the transfer");
    }

    private static ulong CurrentTick(Stopwatch clock) => 100 + (ulong)(clock.Elapsed.TotalSeconds * SimulationClock.AuthoritativeTicksPerSecond);

    // An in-memory duplex link. Server to client runs at bytesPerSecond in 2 KB slices and a server
    // write completes when its last slice is delivered; client to server is immediate.
    private sealed class ThrottledLink : IAsyncDisposable
    {
        private const int SliceBytes = 2048;
        private readonly Channel<byte[]> _toClient = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _toServer = Channel.CreateUnbounded<byte[]>();
        private readonly int _bytesPerSecond;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _nextFreeSeconds;

        public ThrottledLink(string id, int bytesPerSecond)
        {
            _bytesPerSecond = bytesPerSecond;
            Server = new End(id + "-server", this, toClient: true);
            Client = new End(id + "-client", this, toClient: false);
        }

        public ITransportConnection Server { get; }

        public ITransportConnection Client { get; }

        public ValueTask DisposeAsync()
        {
            _toClient.Writer.TryComplete();
            _toServer.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private async ValueTask SendToClientAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            for (int offset = 0; offset < payload.Length; offset += SliceBytes)
            {
                int length = Math.Min(SliceBytes, payload.Length - offset);
                // A busy link keeps its schedule (timer oversleep does not slow it down); an idle
                // one starts from now.
                double now = _clock.Elapsed.TotalSeconds;
                double start = now - _nextFreeSeconds > 0.05 ? now : _nextFreeSeconds;
                double due = start + (double)length / _bytesPerSecond;
                _nextFreeSeconds = due;
                double wait = due - _clock.Elapsed.TotalSeconds;
                if (wait > 0.001)
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken);
                }

                if (!_toClient.Writer.TryWrite(payload.Slice(offset, length).ToArray()))
                {
                    throw new ObjectDisposedException(nameof(ThrottledLink));
                }
            }
        }

        private sealed class End(string id, ThrottledLink link, bool toClient) : ITransportConnection
        {
            private byte[]? _pending;
            private int _pendingOffset;

            public string ConnectionId => id;

            public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            {
                if (toClient)
                {
                    return link.SendToClientAsync(payload, cancellationToken);
                }

                if (!link._toServer.Writer.TryWrite(payload.ToArray()))
                {
                    throw new ObjectDisposedException(nameof(ThrottledLink));
                }

                return ValueTask.CompletedTask;
            }

            public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_pending == null)
                {
                    ChannelReader<byte[]> reader = toClient ? link._toServer.Reader : link._toClient.Reader;
                    if (!await reader.WaitToReadAsync(cancellationToken) || !reader.TryRead(out _pending))
                    {
                        return 0;
                    }

                    _pendingOffset = 0;
                }

                int count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
                _pending.AsMemory(_pendingOffset, count).CopyTo(buffer);
                _pendingOffset += count;
                if (_pendingOffset >= _pending.Length)
                {
                    _pending = null;
                }

                return count;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
