using Dream.Multiplayer.Transport;

namespace Dream.Client.Networking;

public interface IClientTransport
{
    ValueTask<ITransportConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default);
}
