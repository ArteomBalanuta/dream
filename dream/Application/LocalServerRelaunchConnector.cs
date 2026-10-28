using Dream.Client.Networking;
using Dream.Client.Startup;

namespace Dream.Application;

// The connector of the connection screen Game shows after a session to its own integrated server
// ended (that server stopped with the session). The screen keeps the ended endpoint; nothing
// starts until the player connects. A connect to exactly that endpoint starts the new integrated
// server through the fresh local server handle and goes to its port instead (a fresh ephemeral
// one, never the stopped or an abandoned server's). Every other connect passes through unchanged.
public sealed class LocalServerRelaunchConnector : IRemoteSessionConnector
{
    private readonly IRemoteSessionConnector _inner;
    private readonly ILocalServerHandle _localServer;
    private readonly string _endedHost;
    private readonly int _endedPort;

    public LocalServerRelaunchConnector(
        IRemoteSessionConnector inner,
        ILocalServerHandle localServer,
        string endedHost,
        int endedPort)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _localServer = localServer ?? throw new ArgumentNullException(nameof(localServer));
        ArgumentException.ThrowIfNullOrWhiteSpace(endedHost);
        _endedHost = endedHost.Trim();
        _endedPort = endedPort;
    }

    // Starts the server synchronously, on the caller's thread (the update thread, when the player
    // presses Connect), as Use Local Server does.
    public ValueTask<RemoteClientSession> ConnectAsync(
        RemoteClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsEndedEndpoint(options))
        {
            return _inner.ConnectAsync(options, cancellationToken);
        }

        _localServer.EnsureStarted();
        if (!_localServer.IsReady || _localServer.BoundPort <= 0)
        {
            return ValueTask.FromException<RemoteClientSession>(new InvalidOperationException(
                $"Local server failed: {_localServer.ErrorMessage ?? "it did not start"}"));
        }

        return _inner.ConnectAsync(options with { Port = _localServer.BoundPort }, cancellationToken);
    }

    private bool IsEndedEndpoint(RemoteClientOptions options)
    {
        return options.Port == _endedPort
            && string.Equals(options.Host.Trim(), _endedHost, StringComparison.OrdinalIgnoreCase);
    }
}
