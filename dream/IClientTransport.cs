public interface IClientTransport
{
    ValueTask<ITransportConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default);
}
