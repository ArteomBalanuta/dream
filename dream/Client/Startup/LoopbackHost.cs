namespace Dream.Client.Startup;

// The one loopback test of the connection flow: which host names point at this machine. The
// coordinator uses it to decide whether a configured endpoint means the integrated server, and
// Game and LocalServerRelaunchConnector use it to recognize a session to that server, however
// the player spelled the host.
public static class LoopbackHost
{
    public static bool IsLoopback(string? host)
    {
        if (host is null)
        {
            return false;
        }

        // Brackets and a trailing dot are how a host is sometimes typed ("[::1]", "localhost.").
        string trimmed = host.Trim().Trim('[', ']').TrimEnd('.');
        return string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(trimmed, out System.Net.IPAddress? address) && System.Net.IPAddress.IsLoopback(address));
    }
}
