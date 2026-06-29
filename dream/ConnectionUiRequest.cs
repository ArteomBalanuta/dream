public readonly record struct ConnectionUiRequest(
    string Host,
    string PortText,
    string PlayerName,
    bool UseLocalServerPressed,
    bool ConnectPressed);
