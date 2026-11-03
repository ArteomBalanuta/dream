using System.Globalization;
using System.Net;

namespace Dream.Server;

// What an operator sets for a dedicated server run, from the environment (M8):
//   DREAM_PORT          TCP port to listen on, 1 to 65535 (default 5057)
//   DREAM_BIND_ADDRESS  the interface address to bind (default 0.0.0.0, every interface: the dedicated default)
//   DREAM_MAX_PLAYERS   players that may be joined at once (default 64)
//   DREAM_MOTD          one line shown to every player after joining (max 256 characters)
// An unset or blank variable keeps the default. A port or address that is set and invalid is an error:
// the server does not start (ServerBootstrap.InvalidConfigExitCode) instead of listening somewhere the
// operator did not ask for. DREAM_SERVER_PORT is a client setting and is not read here.
public sealed record ServerRuntimeConfig(IPAddress BindAddress, int Port, int MaxPlayers, string? Motd)
{
    public const string PortEnvironmentVariable = "DREAM_PORT";
    public const string BindAddressEnvironmentVariable = "DREAM_BIND_ADDRESS";
    public const string MaxPlayersEnvironmentVariable = "DREAM_MAX_PLAYERS";
    public const string MotdEnvironmentVariable = "DREAM_MOTD";

    public const int DefaultMaxPlayers = 64;

    public const int MaxMotdLength = 256;

    public static ServerRuntimeConfig Default => new(IPAddress.Any, ServerBootstrap.DefaultPort, DefaultMaxPlayers, null);

    // Reads the process environment. Null after logging why when a value is invalid.
    public static ServerRuntimeConfig? FromEnvironment(Action<string>? log) =>
        Parse(Environment.GetEnvironmentVariable, log);

    // The same over any source of variables (tests give their own; nothing is bound here).
    public static ServerRuntimeConfig? Parse(Func<string, string?> read, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(read);

        int port = ServerBootstrap.DefaultPort;
        string? portText = read(PortEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(portText))
        {
            if (!int.TryParse(portText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535)
            {
                log?.Invoke($"{PortEnvironmentVariable} must be a whole number from 1 to 65535; got '{Shown(portText)}'. Server not started.");
                return null;
            }
        }

        IPAddress address = IPAddress.Any;
        string? addressText = read(BindAddressEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(addressText))
        {
            if (!IPAddress.TryParse(addressText.Trim(), out IPAddress? parsed))
            {
                log?.Invoke($"{BindAddressEnvironmentVariable} must be an IPv4 or IPv6 address; got '{Shown(addressText)}'. Server not started.");
                return null;
            }

            address = parsed;
        }

        int maxPlayers = DefaultMaxPlayers;
        string? maxText = read(MaxPlayersEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(maxText))
        {
            if (int.TryParse(maxText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int max) && max > 0)
            {
                maxPlayers = max;
            }
            else
            {
                log?.Invoke($"{MaxPlayersEnvironmentVariable} is not a positive integer; keeping {DefaultMaxPlayers}.");
            }
        }

        return new ServerRuntimeConfig(address, port, maxPlayers, SanitizeMotd(read(MotdEnvironmentVariable)));
    }

    // One line: control and bidi characters stripped, trimmed, cut to MaxMotdLength. Null when nothing is left.
    public static string? SanitizeMotd(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string cleaned = Dream.Multiplayer.Protocol.ChatMessageHandler.Sanitize(value).Trim();
        if (cleaned.Length > MaxMotdLength)
        {
            cleaned = cleaned[..MaxMotdLength];
            if (char.IsHighSurrogate(cleaned[^1]))
            {
                cleaned = cleaned[..^1];
            }
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    // A value is echoed in the log, so it is cut and stripped of control characters.
    private static string Shown(string value)
    {
        string cleaned = Dream.Multiplayer.Protocol.ChatMessageHandler.Sanitize(value).Trim();
        return cleaned.Length > 64 ? cleaned[..64] + "..." : cleaned;
    }
}
