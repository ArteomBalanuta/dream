public sealed class RemoteSessionConnector : IRemoteSessionConnector
{
    public ValueTask<RemoteClientSession> ConnectAsync(
        RemoteClientOptions options,
        CancellationToken cancellationToken = default)
    {
        return RemoteClientSession.ConnectAsync(options, cancellationToken: cancellationToken);
    }
}
