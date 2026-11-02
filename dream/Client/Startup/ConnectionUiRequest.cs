namespace Dream.Client.Startup;

public readonly record struct ConnectionUiRequest(
    string Host,
    string PortText,
    string PlayerName,
    bool UseLocalServerPressed,
    bool ConnectPressed,
    bool BackPressed = false);
