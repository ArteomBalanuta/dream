using System.Net;

namespace Dream.Client.Startup;

// Where the integrated server listens. Loopback is Single Player (ephemeral port, loopback-only liveness
// rules); Hosted binds every interface on a chosen port with the dedicated server's connection rules.
public sealed record LocalServerListenOptions(IPAddress BindAddress, int Port, bool AllowRemotePlayers)
{
    public static LocalServerListenOptions Loopback { get; } = new(IPAddress.Loopback, 0, false);

    public static LocalServerListenOptions Hosted(int port) => new(IPAddress.Any, port, true);

    // 1..65535, invariant culture, trimmed.
    public static bool TryParsePort(string? text, out int port)
    {
        port = 0;
        return text != null
            && int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;
    }
}
