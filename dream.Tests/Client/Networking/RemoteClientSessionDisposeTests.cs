using System.Buffers;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;

namespace Dream.Tests.Client.Networking;

// A direct Send*Async writes the session's pooled frame buffer straight to the transport. Dispose
// must not hand that buffer back to ArrayPool<byte>.Shared while such a send is still in flight,
// or another renter can overwrite the frame mid-write.
public class RemoteClientSessionDisposeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DisposeAsync_WaitsForInFlightDirectSend_AndFrameBytesStayIntact()
    {
        BlockingTransportConnection connection = new();
        RemoteClientSession session = CreateSession(connection);

        // Everything up to the first await runs on this thread, so a buffer the session returns
        // to the pool lands in this thread's ArrayPool cache, where the Rent below would get it.
        ValueTask send = session.SendHelloAsync();
        Assert.True(connection.SendStarted.IsCompleted);
        Assert.False(send.IsCompleted);

        ValueTask dispose = session.DisposeAsync();

        // Play "another renter" of the shared pool and scribble over whatever it hands out.
        byte[][] rented = new byte[4][];
        for (int i = 0; i < rented.Length; i++)
        {
            rented[i] = ArrayPool<byte>.Shared.Rent(PooledFrameWriter.DefaultInitialCapacity);
            rented[i].AsSpan().Fill(0xAB);
        }

        try
        {
            Assert.False(dispose.IsCompleted, "DisposeAsync completed while a direct send was still writing the pooled frame.");

            connection.ReleaseSend();
            await send.AsTask().WaitAsync(Timeout);
            await dispose.AsTask().WaitAsync(Timeout);
        }
        finally
        {
            foreach (byte[] array in rented)
            {
                ArrayPool<byte>.Shared.Return(array);
            }
        }

        byte[] atStart = Assert.Single(connection.BytesAtSendStart);
        byte[] atEnd = Assert.Single(connection.BytesAtSendEnd);
        Assert.Equal(atStart, atEnd);

        LengthPrefixedMessageFramer framer = new();
        Assert.True(framer.TryDeframe(atEnd, out byte[]? payload, out _));
        DecodedProtocolMessage decoded = new TransportJsonCodec().Decode(payload!);
        Assert.Equal(1ul, decoded.Envelope.Sequence);
        Assert.Equal("player-a", Assert.IsType<ClientHelloMessage>(decoded.Message).ClientName);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task DirectSend_AfterDispose_ThrowsObjectDisposedException()
    {
        BlockingTransportConnection connection = new();
        connection.ReleaseSend();
        RemoteClientSession session = CreateSession(connection);
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SendHelloAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SendJoinRequestAsync().AsTask());
        Assert.Empty(connection.BytesAtSendStart);
    }

    [Fact]
    public async Task DirectSend_WaitingForTheLockWhenDisposeStarts_ThrowsObjectDisposedException()
    {
        BlockingTransportConnection connection = new();
        RemoteClientSession session = CreateSession(connection);

        ValueTask first = session.SendHelloAsync();
        ValueTask second = session.SendJoinRequestAsync();
        Assert.False(second.IsCompleted);

        ValueTask dispose = session.DisposeAsync();
        connection.ReleaseSend();

        await first.AsTask().WaitAsync(Timeout);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second.AsTask().WaitAsync(Timeout));
        await dispose.AsTask().WaitAsync(Timeout);
        Assert.Single(connection.BytesAtSendStart);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        BlockingTransportConnection connection = new();
        RemoteClientSession session = CreateSession(connection);
        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.True(connection.Disposed);
    }

    private static RemoteClientSession CreateSession(ITransportConnection connection)
    {
        return new RemoteClientSession(
            connection,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 5057, "session-a", "player-a", "player-a"));
    }

    private sealed class BlockingTransportConnection : ITransportConnection
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ConnectionId => "blocking";

        public TaskCompletionSource SendStartedSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendStarted => SendStartedSource.Task;

        public List<byte[]> BytesAtSendStart { get; } = [];

        public List<byte[]> BytesAtSendEnd { get; } = [];

        public bool Disposed { get; private set; }

        public void ReleaseSend()
        {
            _gate.TrySetResult();
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            lock (BytesAtSendStart)
            {
                BytesAtSendStart.Add(payload.ToArray());
            }

            SendStartedSource.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken);
            lock (BytesAtSendEnd)
            {
                BytesAtSendEnd.Add(payload.ToArray());
            }
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(0);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
