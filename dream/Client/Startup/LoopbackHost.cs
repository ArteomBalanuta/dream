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

        string trimmed = host.Trim();
        return string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "::1", StringComparison.OrdinalIgnoreCase);
    }
}
