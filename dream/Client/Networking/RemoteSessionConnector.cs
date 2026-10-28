using Dream.Multiplayer.Protocol;

namespace Dream.Client.Networking;

public sealed class RemoteSessionConnector : IRemoteSessionConnector
{
    private readonly FrameLimitOptions? _frameLimits;

    // Null frame limits read FrameLimitOptions.FromEnvironment on each connect.
    public RemoteSessionConnector(FrameLimitOptions? frameLimits = null)
    {
        _frameLimits = frameLimits;
    }

    public ValueTask<RemoteClientSession> ConnectAsync(
        RemoteClientOptions options,
        CancellationToken cancellationToken = default)
    {
        // The client reads with the large server-to-client cap (a whole world arrives in one
        // JoinAccepted frame) and writes with the small client-to-server cap the server reads with.
        FrameLimitOptions limits = _frameLimits ?? FrameLimitOptions.FromEnvironment(static line => Console.Error.WriteLine(line));
        return RemoteClientSession.ConnectAsync(
            options,
            framer: limits.CreateClientFramer(),
            cancellationToken: cancellationToken);
    }
}
