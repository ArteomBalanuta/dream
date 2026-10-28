using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;

namespace Dream.Tests.Server;

public class ServerConnectionTrafficCounterTests
{
    [Fact]
    public async Task SentFrames_AreCountedWithTheirFramedLength()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        using DedicatedServerHost host = DedicatedServerHost.CreateDefault();
        RecordingConnection transport = new();
        await using ServerConnection connection = new(transport, host, codec, framer);

        connection.Enqueue(new ServerHelloMessage("server", TransportEnvelope.CurrentProtocolVersion, CurrentServerTick: 0));
        connection.Enqueue(new JoinRejectedMessage("full"));
        await connection.SendJoinRejectedAsync(new JoinRejectedMessage("direct"));
        Assert.True(await connection.FlushOutboundAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(3, transport.Sent.Count);
        Assert.Equal(3, connection.FramesSent);
        Assert.Equal(transport.Sent.Sum(frame => (long)frame.Length), connection.BytesSent);
        Assert.Equal(0, connection.CoalescedSnapshots);
        Assert.Equal(0, connection.BytesReceived);
    }

    [Fact]
    public async Task ReceivedBytesAndFrames_AreCounted()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        using DedicatedServerHost host = DedicatedServerHost.CreateDefault();
        byte[] hello = framer.Frame(codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "client.hello", 1),
            new ClientHelloMessage("bot")));
        byte[] twoFrames = [.. hello, .. hello];
        RecordingConnection transport = new(twoFrames);
        await using ServerConnection connection = new(transport, host, codec, framer);

        Assert.True(await connection.ProcessIncomingAsync());
        Assert.True(await connection.FlushOutboundAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(twoFrames.Length, connection.BytesReceived);
        Assert.Equal(2, connection.FramesReceived);
        // Each hello is answered with a server hello.
        Assert.Equal(2, connection.FramesSent);
        Assert.Equal(transport.Sent.Sum(frame => (long)frame.Length), connection.BytesSent);
    }

    private sealed class RecordingConnection : ITransportConnection
    {
        private readonly object _sync = new();
        private readonly List<byte[]> _sent = [];
        private byte[]? _inbound;

        public RecordingConnection(byte[]? inbound = null)
        {
            _inbound = inbound;
        }

        public string ConnectionId => "counter-connection";

        public IReadOnlyList<byte[]> Sent
        {
            get
            {
                lock (_sync)
                {
                    return _sent.ToArray();
                }
            }
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[]? inbound = Interlocked.Exchange(ref _inbound, null);
            if (inbound == null)
            {
                return ValueTask.FromResult(0);
            }

            inbound.CopyTo(buffer);
            return ValueTask.FromResult(inbound.Length);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _sent.Add(payload.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
