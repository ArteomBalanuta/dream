using System.Net.Sockets;

public sealed class TcpClientTransport : ITransportConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public TcpClientTransport(TcpClient client, string? connectionId = null)
    {
        _client = client;
        _stream = client.GetStream();
        ConnectionId = connectionId ?? client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString("N");
    }

    public string ConnectionId { get; }

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

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
