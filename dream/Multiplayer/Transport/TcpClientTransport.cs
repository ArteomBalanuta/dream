using System.Net.Sockets;

namespace Dream.Multiplayer.Transport;


public sealed class TcpClientTransport : ITransportConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public TcpClientTransport(TcpClient client, string? connectionId = null)
    {
        _client = client;
        // Commands and snapshots are small, latency-sensitive writes; Nagle batching plus delayed
        // ACK held them back in bursts.
        _client.NoDelay = true;
        _stream = client.GetStream();
        ConnectionId = connectionId ?? client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString("N");
    }

    public string ConnectionId { get; }

    public bool NoDelay => _client.NoDelay;

    public static async ValueTask<TcpClientTransport> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        TcpClient client = new();
        using CancellationTokenRegistration registration = cancellationToken.Register(() => client.Dispose());
        await client.ConnectAsync(host, port, cancellationToken);
        return new TcpClientTransport(client);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        return _stream.WriteAsync(payload, cancellationToken);
    }

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return _stream.ReadAsync(buffer, cancellationToken);
    }

    // Sends a FIN before the close, so the peer's pending read usually ends as a normal close
    // instead of a reset. Windows still resets a socket closed with bytes unread, which
    // DedicatedServerService classifies as a normal disconnect (IsPeerReset).
    public ValueTask DisposeAsync()
    {
        try
        {
            // Null once the client was disposed (a second DisposeAsync).
            _client.Client?.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Already closed, reset or never connected: nothing to tell the peer.
        }

        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
