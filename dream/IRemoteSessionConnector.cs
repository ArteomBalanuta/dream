public interface IRemoteSessionConnector
{
    ValueTask<RemoteClientSession> ConnectAsync(
        RemoteClientOptions options,
        CancellationToken cancellationToken = default);
}
