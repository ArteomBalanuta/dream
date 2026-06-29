public interface IServerTransport : IAsyncDisposable
{
    int Port { get; }

    void Start();

    ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default);
}
