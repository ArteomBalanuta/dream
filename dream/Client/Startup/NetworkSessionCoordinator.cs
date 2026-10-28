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
            : !_localServer.IsStarted
                ? LocalServerNotStartedMessage
                : _localServer.IsReady
                    ? $"Local server ready on 127.0.0.1:{_localServer.BoundPort}"
                    : _localServer.StartingStatus ?? "Starting local server...";

    private const string LocalServerNotStartedMessage =
        "Local server not started; it starts on Use Local Server or a localhost connect.";

    // The integrated server runs a full authoritative world, so it starts only when local hosting
    // is actually used instead of on every launch.
    public void EnsureLocalServerStarted()
    {
        if (_localServer.IsStarted)
        {
            return;
        }

        _localServer.EnsureStarted();
        RefreshIdleState();
    }

    public ClientConnectionOptions UseLocalServerDefaults(string playerName)
    {
        EnsureLocalServerStarted();
        int localPort = _localServer.BoundPort > 0 ? _localServer.BoundPort : ServerBootstrap.DefaultPort;
        return new ClientConnectionOptions("127.0.0.1", localPort.ToString(), playerName);
    }

    public ClientConnectionOptions ResolveInitialOptions(ClientConnectionOptions configuredOptions)
    {
        ArgumentNullException.ThrowIfNull(configuredOptions);

        if (!IsLoopbackHost(configuredOptions.Host))
        {
            return configuredOptions;
        }

        EnsureLocalServerStarted();
        return _localServer.IsReady
            ? UseLocalServerDefaults(configuredOptions.PlayerName)
            : configuredOptions;
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

    // Shows why the previous play session ended (a lost connection, a silent server, a failed
    // join) on the connection screen Game returns to. The flow is idle again, so Connect and Use
    // Local Server work as usual and replace the message.
    public void ReportSessionEnded(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (IsBusy)
        {
            return;
        }

        State = ConnectionFlowState.Failed;
        StatusMessage = $"Session ended: {reason}";
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
            RemoteClientSession session = await _connector.ConnectAsync(options);
            if (!session.IsConnected && session.DisconnectReason is string reason)
            {
                // Ended before the play scene could take it, e.g. a join rejected during the
                // connect: stay on the connection screen with the reason.
                await session.DisposeAsync();
                State = ConnectionFlowState.Failed;
                StatusMessage = reason;
                return;
            }

            _connectedSession = session;
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

        if (!_localServer.IsStarted)
        {
            State = ConnectionFlowState.ReadyToConnect;
            StatusMessage = LocalServerNotStartedMessage;
            return;
        }

        if (_localServer.IsReady)
        {
            State = ConnectionFlowState.ReadyToConnect;
            StatusMessage = $"Local server ready on 127.0.0.1:{_localServer.BoundPort}.";
            return;
        }

        State = ConnectionFlowState.WaitingForLocalServer;
        StatusMessage = _localServer.StartingStatus ?? "Starting local server...";
    }

    private static bool IsLoopbackHost(string host) => LoopbackHost.IsLoopback(host);
}
