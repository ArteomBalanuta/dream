using Dream.Server;

namespace Dream.Client.Networking;

public sealed record RemoteClientOptions(
    string Host,
    int Port,
    string SessionId,
    string DisplayName,
    string ClientName,
    string? AccountToken = null)
{
    // Ask the server to keep this player's spawn protection until their first input (the loading
    // screen's prompt). Sent with the join request; off sends nothing.
    public bool HoldSpawnProtection { get; init; }

    // Prints everything but the token: the token is a secret and must never reach a log or message.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Host = ").Append(Host)
            .Append(", Port = ").Append(Port)
            .Append(", SessionId = ").Append(SessionId)
            .Append(", DisplayName = ").Append(DisplayName)
            .Append(", ClientName = ").Append(ClientName);
        return true;
    }

    // The port a client assumes when nothing else says otherwise. Mirrors the server's own default
    // from the one allowlisted place the client may read it, so the rest of the client (the menu's
    // join/host form, for one) does not have to reach into Dream.Server for a number.
    public const int DefaultPort = ServerBootstrap.DefaultPort;

    public const string HostEnvironmentVariable = "DREAM_SERVER_HOST";
    public const string PortEnvironmentVariable = "DREAM_SERVER_PORT";
    public const string SessionIdEnvironmentVariable = "DREAM_CLIENT_SESSION_ID";
    public const string DisplayNameEnvironmentVariable = "DREAM_CLIENT_NAME";

    public string EndpointLabel => $"{Host}:{Port}";

    public static bool TryResolveFromEnvironment(out RemoteClientOptions? options)
    {
        string? host = Environment.GetEnvironmentVariable(HostEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(host))
        {
            options = null;
            return false;
        }

        string? rawPort = Environment.GetEnvironmentVariable(PortEnvironmentVariable);
        int port = ServerBootstrap.DefaultPort;
        if (!string.IsNullOrWhiteSpace(rawPort) && !int.TryParse(rawPort, out port))
        {
            throw new InvalidOperationException(
                $"Environment variable '{PortEnvironmentVariable}' must be a valid integer port.");
        }

        string sessionId = Environment.GetEnvironmentVariable(SessionIdEnvironmentVariable)
            ?? $"client-{Guid.NewGuid():N}";
        string displayName = Environment.GetEnvironmentVariable(DisplayNameEnvironmentVariable)
            ?? "dream-client";

        options = new RemoteClientOptions(host.Trim(), port, sessionId, displayName, displayName);
        return true;
    }
}
