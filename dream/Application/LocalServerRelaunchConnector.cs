using Dream.Client.Networking;
using Dream.Client.Startup;

namespace Dream.Application;

// The connector of the connection screen Game shows after a session to its own integrated server
// ended (that server stopped with the session; PlaySessionEndTransition.RelaunchTarget). The
// screen keeps the ended endpoint; nothing starts until the player connects. A connect to that
// endpoint (the same port on any loopback spelling: localhost, 127.0.0.1, ::1) starts the new
// integrated server through the fresh local server handle and goes to 127.0.0.1 on its port
// instead (a fresh ephemeral one, never the stopped or an abandoned server's). Every other
// connect passes through unchanged.
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

    // Requests the start on the caller's thread (the update thread, when the player presses
    // Connect), as Use Local Server does. When the previous integrated server is still saving to
    // the shared world database, the handle queues the start behind it instead of blocking, and
    // the connect waits here, off the update thread, while the connection screen shows that the
    // previous server is still saving.
    public async ValueTask<RemoteClientSession> ConnectAsync(
        RemoteClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsEndedEndpoint(options))
        {
            return await _inner.ConnectAsync(options, cancellationToken);
        }

        _localServer.EnsureStarted();
        await _localServer.WaitForStartAsync().WaitAsync(cancellationToken);
        if (!_localServer.IsReady || _localServer.BoundPort <= 0)
        {
            throw new InvalidOperationException(
                $"Local server failed: {_localServer.ErrorMessage ?? "it did not start"}");
        }

        return await _inner.ConnectAsync(
            options with { Host = "127.0.0.1", Port = _localServer.BoundPort },
            cancellationToken);
    }

    private bool IsEndedEndpoint(RemoteClientOptions options)
    {
        if (options.Port != _endedPort)
        {
            return false;
        }

        return LoopbackHost.IsLoopback(_endedHost)
            ? LoopbackHost.IsLoopback(options.Host)
            : string.Equals(options.Host.Trim(), _endedHost, StringComparison.OrdinalIgnoreCase);
    }
}
