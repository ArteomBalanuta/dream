using System.Diagnostics;
using System.Threading.Channels;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Server;

// Wave 7 tester (SCALE-6): the service totals must match the bytes and frames that really crossed
// each transport, keep a disconnected connection's traffic, and resetWindow must clear only the
// windowed histogram.
public class Wave7ServiceMetricsAcrossConnectionsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Totals_MatchTransportBytes_AcrossJoinsAndADisconnect_AndResetWindowKeepsTotals()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        ScriptedServerTransport transport = new();
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        await using DedicatedServerService service = new(host, transport, codec, framer);
        service.Start();

        ScriptedConnection alpha = new("alpha");
        ScriptedConnection beta = new("beta");
        foreach (ScriptedConnection connection in new[] { alpha, beta })
        {
            transport.Offer(connection);
            await service.AcceptNextClientAsync();
            connection.Push(framer.Frame(codec.Encode(
                new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "client.hello", 1),
                new ClientHelloMessage(connection.ConnectionId))));
            connection.Push(framer.Frame(codec.Encode(
                new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "client.join-request", 2),
                new ClientJoinRequestMessage(connection.ConnectionId + "-session", connection.ConnectionId))));
        }

        await WaitUntil(() => service.CaptureMetrics().FramesReceived == 4, "both hellos and joins received");
        await WaitUntil(() => service.CaptureMetrics().JoinedConnections == 2, "both connections joined");
        for (int i = 0; i < 6; i++)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        }

        Assert.True(await service.FlushOutboundAsync(Deadline));
        ServerMetricsSnapshot joined = service.CaptureMetrics();
        Assert.Equal(2, joined.Connections);
        Assert.Equal(alpha.SentBytes + beta.SentBytes, joined.BytesSent);
        Assert.Equal(alpha.SentFrames + beta.SentFrames, joined.FramesSent);
        Assert.Equal(alpha.PushedBytes + beta.PushedBytes, joined.BytesReceived);
        // Hello and join replies plus at least one snapshot each (queued snapshots may coalesce).
        Assert.True(joined.FramesSent > 4, $"{joined.FramesSent} frames");

        // Alpha disconnects: its traffic must stay in the totals once the connection is retired.
        long alphaSentAtClose = alpha.SentBytes;
        alpha.Close();
        await WaitUntil(() => service.CaptureMetrics().Connections == 1, "alpha retired");
        for (int i = 0; i < 3; i++)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        }

        Assert.True(await service.FlushOutboundAsync(Deadline));
        ServerMetricsSnapshot afterDisconnect = service.CaptureMetrics();
        Assert.Equal(1, afterDisconnect.JoinedConnections);
        Assert.Equal(alphaSentAtClose, alpha.SentBytes);
        Assert.Equal(alpha.SentBytes + beta.SentBytes, afterDisconnect.BytesSent);
        Assert.Equal(alpha.SentFrames + beta.SentFrames, afterDisconnect.FramesSent);
        Assert.Equal(alpha.PushedBytes + beta.PushedBytes, afterDisconnect.BytesReceived);
        Assert.Equal(joined.FramesReceived, afterDisconnect.FramesReceived);
        Assert.True(afterDisconnect.BytesSent > joined.BytesSent);

        // resetWindow copies, then clears only the window; totals and the since-start histogram stay.
        ServerMetricsSnapshot beforeReset = service.CaptureMetrics(resetWindow: true);
        Assert.True(beforeReset.Tick.TickWindow.Count > 0);
        ServerMetricsSnapshot afterReset = service.CaptureMetrics();
        Assert.Equal(0, afterReset.Tick.TickWindow.Count);
        Assert.Equal(beforeReset.Tick.Tick.Count, afterReset.Tick.Tick.Count);
        Assert.Equal(beforeReset.Tick.SimulatedTicks, afterReset.Tick.SimulatedTicks);
        Assert.Equal(beforeReset.BytesSent, afterReset.BytesSent);
        Assert.Equal(beforeReset.FramesSent, afterReset.FramesSent);

        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        ServerMetricsSnapshot nextWindow = service.CaptureMetrics();
        Assert.Equal(1, nextWindow.Tick.TickWindow.Count);
        Assert.Equal(beforeReset.Tick.Tick.Count + 1, nextWindow.Tick.Tick.Count);
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    private sealed class ScriptedServerTransport : IServerTransport
    {
        private readonly Channel<ITransportConnection> _pending = Channel.CreateUnbounded<ITransportConnection>();

        public int Port => 0;

        public void Offer(ITransportConnection connection)
        {
            _pending.Writer.TryWrite(connection);
        }

        public void Start()
        {
        }

        public ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default)
        {
            return _pending.Reader.ReadAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedConnection(string connectionId) : ITransportConnection
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly object _sync = new();
        private long _sentBytes;
        private long _sentFrames;
        private long _pushedBytes;
        private byte[]? _partial;
        private int _partialOffset;

        public string ConnectionId { get; } = connectionId;

        public long SentBytes
        {
            get
            {
                lock (_sync)
                {
                    return _sentBytes;
                }
            }
        }

        public long SentFrames
        {
            get
            {
                lock (_sync)
                {
                    return _sentFrames;
                }
            }
        }

        public long PushedBytes => Interlocked.Read(ref _pushedBytes);

        public void Push(byte[] frame)
        {
            Interlocked.Add(ref _pushedBytes, frame.Length);
            _inbound.Writer.TryWrite(frame);
        }

        public void Close()
        {
            _inbound.Writer.TryComplete();
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _sentBytes += payload.Length;
                _sentFrames++;
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_partial == null)
            {
                if (!await _inbound.Reader.WaitToReadAsync(cancellationToken) || !_inbound.Reader.TryRead(out _partial))
                {
                    return 0;
                }

                _partialOffset = 0;
            }

            int count = Math.Min(buffer.Length, _partial.Length - _partialOffset);
            _partial.AsMemory(_partialOffset, count).CopyTo(buffer);
            _partialOffset += count;
            if (_partialOffset >= _partial.Length)
            {
                _partial = null;
            }

            return count;
        }

        public ValueTask DisposeAsync()
        {
            Close();
            return ValueTask.CompletedTask;
        }
    }
}
