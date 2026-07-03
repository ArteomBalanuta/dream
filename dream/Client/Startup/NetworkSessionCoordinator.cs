using Dream.Client.Networking;
using Dream.Server;

namespace Dream.Client.Startup;

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

    public bool IsLocalServerReady => _localServer.IsReady;

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

    public ClientConnectionOptions ResolveInitialOptions(ClientConnectionOptions configuredOptions)
    {
        ArgumentNullException.ThrowIfNull(configuredOptions);

        if (IsLoopbackHost(configuredOptions.Host) && _localServer.IsReady)
        {
            return UseLocalServerDefaults(configuredOptions.PlayerName);
        }

        return configuredOptions;
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

    private static bool IsLoopbackHost(string host)
    {
        return string.Equals(host.Trim(), "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host.Trim(), "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host.Trim(), "::1", StringComparison.OrdinalIgnoreCase);
    }
}
