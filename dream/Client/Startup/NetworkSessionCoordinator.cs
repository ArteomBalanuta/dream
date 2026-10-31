using Dream.Client.Identity;
using Dream.Client.Networking;
using Dream.Server;

namespace Dream.Client.Startup;

public sealed class NetworkSessionCoordinator
{
    private readonly ILocalServerHandle _localServer;
    private readonly IRemoteSessionConnector _connector;
    private Task? _pendingConnectionTask;
    private RemoteClientSession? _connectedSession;

    private readonly string? _contentError;
    private readonly IClientIdentityStore? _identity;

    // contentError (ClientContentCheck) is why the game's content files cannot be used; with it the
    // connection screen shows the message, no local server starts and Connect does nothing.
    public NetworkSessionCoordinator(
        ILocalServerHandle localServer,
        IRemoteSessionConnector connector,
        string? contentError = null,
        IClientIdentityStore? identity = null)
    {
        // A null identity store leaves the join token null, so a test coordinator never touches the
        // real token file.
        _identity = identity;
        _localServer = localServer;
        _connector = connector;
        _contentError = contentError;
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
        if (_contentError != null || _localServer.IsStarted)
        {
            return;
        }

        _localServer.EnsureStarted();
        RefreshIdleState();
    }

    // The Use Local Server button: starts the local server (again, when the last start failed, so a
    // second window can host after the first one closed) and returns its endpoint. False, with the
    // status refreshed, when the server is not ready: the caller then leaves the host and port the
    // player entered alone, and nothing is persisted.
    public bool TryUseLocalServer(string playerName, out ClientConnectionOptions? options)
    {
        // A connect is in flight: refreshing the idle state would flip Connecting to ReadyToConnect and
        // let a second BeginConnect run beside it (one session would leak).
        if (IsBusy)
        {
            options = null;
            return false;
        }

        if (_localServer.ErrorMessage != null)
        {
            _localServer.ResetFailure();
        }

        EnsureLocalServerStarted();
        RefreshIdleState();
        if (!_localServer.IsReady)
        {
            options = null;
            return false;
        }

        options = UseLocalServerDefaults(playerName);
        return true;
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

        if (_contentError != null)
        {
            State = ConnectionFlowState.Failed;
            StatusMessage = _contentError;
            _pendingConnectionTask = Task.CompletedTask;
            return;
        }

        if (!options.TryCreateRemoteOptions(out RemoteClientOptions? resolved, out string? error))
        {
            State = ConnectionFlowState.Failed;
            StatusMessage = error ?? "Connection settings are invalid.";
            _pendingConnectionTask = Task.CompletedTask;
            return;
        }

        if (_identity != null)
        {
            try
            {
                resolved = resolved! with { AccountToken = _identity.GetOrCreateToken(ServerKeyFor(resolved)) };
            }
            catch (IOException ex)
            {
                State = ConnectionFlowState.Failed;
                StatusMessage = ex.Message;
                _pendingConnectionTask = Task.CompletedTask;
                return;
            }
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
        if (_contentError != null)
        {
            State = ConnectionFlowState.Failed;
            StatusMessage = _contentError;
            return;
        }

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

    // "local" for every loopback address and port (the integrated server on whatever port it gets, a
    // dedicated server on this computer, another window's server), else host:port. One person keeps one
    // account across them.
    private string ServerKeyFor(RemoteClientOptions options) =>
        ClientIdentityStore.ServerKeyFor(options.Host, options.Port, IsLoopbackHost(options.Host));

    private static bool IsLoopbackHost(string host) => LoopbackHost.IsLoopback(host);
}
