using Dream.Multiplayer.Transport;

namespace Dream.Client.Networking;

public sealed class TcpClientConnector : IClientTransport
{
    public ValueTask<ITransportConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        return ConnectInternalAsync(host, port, cancellationToken);
    }

    private static async ValueTask<ITransportConnection> ConnectInternalAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        return await TcpClientTransport.ConnectAsync(host, port, cancellationToken);
    }
}
