public sealed class NetworkSessionCoordinator
{
    private readonly ILocalServerHandle _localServer;
    private readonly IRemoteSessionConnector _connector;
    private Task? _pendingConnectionTask;
    private RemoteClientSession? _connectedSession;

    public NetworkSessionCoordinator(
        ILocalServerHandle localServer,
        IRemoteSessionConnector connector)
    {
        _localServer = localServer;
        _connector = connector;
        RefreshIdleState();
    }

    public ConnectionFlowState State { get; private set; }

    public string StatusMessage { get; private set; } = string.Empty;

    public bool IsBusy => State == ConnectionFlowState.Connecting;

    public string LocalServerStatus =>
        _localServer.ErrorMessage != null
            ? $"Local server failed: {_localServer.ErrorMessage}"
            : _localServer.IsReady
                ? $"Local server ready on 127.0.0.1:{_localServer.BoundPort}"
                : "Starting local server...";

    public ClientConnectionOptions UseLocalServerDefaults(string playerName)
    {
        int localPort = _localServer.BoundPort > 0 ? _localServer.BoundPort : ServerBootstrap.DefaultPort;
        return new ClientConnectionOptions("127.0.0.1", localPort.ToString(), playerName);
    }

    public void BeginConnect(ClientConnectionOptions options)
    {
        if (IsBusy)
        {
            return;
        }

        if (!options.TryCreateRemoteOptions(out RemoteClientOptions? resolved, out string? error))
        {
            State = ConnectionFlowState.Failed;
            StatusMessage = error ?? "Connection settings are invalid.";
            _pendingConnectionTask = Task.CompletedTask;
            return;
        }

        State = ConnectionFlowState.Connecting;
        StatusMessage = $"Connecting to {resolved!.Host}:{resolved.Port}...";
        _pendingConnectionTask = ConnectInternalAsync(resolved);
    }

    public Task WaitForPendingConnectionAsync()
    {
        return _pendingConnectionTask ?? Task.CompletedTask;
    }

    public bool TryTakeConnectedSession(out RemoteClientSession? session)
    {
        session = _connectedSession;
        _connectedSession = null;
        return session != null;
    }

    private async Task ConnectInternalAsync(RemoteClientOptions options)
    {
        try
        {
            _connectedSession = await _connector.ConnectAsync(options);
            State = ConnectionFlowState.Connected;
            StatusMessage = $"Connected to {options.Host}:{options.Port}.";
        }
        catch (Exception ex)
        {
            State = ConnectionFlowState.Failed;
            StatusMessage = ex.Message;
        }
    }

    private void RefreshIdleState()
    {
        if (_localServer.ErrorMessage != null)
        {
            State = ConnectionFlowState.Failed;
            StatusMessage = _localServer.ErrorMessage;
            return;
        }

        if (_localServer.IsReady)
        {
            State = ConnectionFlowState.ReadyToConnect;
            StatusMessage = $"Local server ready on 127.0.0.1:{_localServer.BoundPort}.";
            return;
        }

        State = ConnectionFlowState.WaitingForLocalServer;
        StatusMessage = "Starting local server...";
    }
}
