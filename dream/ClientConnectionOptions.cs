public sealed record ClientConnectionOptions(
    string Host,
    string PortText,
    string PlayerName)
{
    public bool TryCreateRemoteOptions(out RemoteClientOptions? options, out string? error)
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            options = null;
            error = "Host is required.";
            return false;
        }

        if (!int.TryParse(PortText, out int port) || port < 1 || port > 65535)
        {
            options = null;
            error = "Port must be a valid integer between 1 and 65535.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(PlayerName))
        {
            options = null;
            error = "Player name is required.";
            return false;
        }

        string trimmedPlayerName = PlayerName.Trim();
        options = new RemoteClientOptions(
            Host.Trim(),
            port,
            $"client-{Guid.NewGuid():N}",
            trimmedPlayerName,
            trimmedPlayerName);
        error = null;
        return true;
    }
}
