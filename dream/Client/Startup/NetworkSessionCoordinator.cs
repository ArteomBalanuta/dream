using Dream.Client.Identity;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Server;

namespace Dream.Client.Startup;

public sealed class NetworkSessionCoordinator
{
    private readonly ILocalServerHandle _localServer;
    private readonly IRemoteSessionConnector _connector;
    private Task? _pendingConnectionTask;
    private RemoteClientSession? _connectedSession;

    private readonly ICharacterListSource? _characterList;
    private readonly string? _contentError;
    private readonly IClientIdentityStore? _identity;

    // contentError (ClientContentCheck) is why the game's content files cannot be used; with it no
    // local server starts and Use Local Server shows the message. A remote connect is unaffected.
    public NetworkSessionCoordinator(
        ILocalServerHandle localServer,
        IRemoteSessionConnector connector,
        string? contentError = null,
        IClientIdentityStore? identity = null,
        ICharacterListSource? characterList = null)
    {
        // Production (a real identity store) asks the server for the account's characters; a test coordinator
        // has no identity and no token to ask with, so it gets no source unless it passes one.
        _characterList = characterList ?? (identity != null ? new CharacterListClient() : null);
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
        if (_contentError != null)
        {
            // The only action the content error blocks: the in-process server reads the catalog.
            State = ConnectionFlowState.Failed;
            StatusMessage = _contentError;
            options = null;
            return false;
        }

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

        ClearPicker();
        if (!TryResolveRemoteOptions(options, out RemoteClientOptions? resolved))
        {
            return;
        }

        State = ConnectionFlowState.Connecting;
        StatusMessage = $"Connecting to {resolved!.Host}:{resolved.Port}...";
        _pendingConnectionTask = ConnectInternalAsync(resolved);
    }

    // The characters the picker shows (M1-C), set while the picker is open (State is ReadyToConnect); null otherwise.
    public IReadOnlyList<CharacterListEntry>? PickerCharacters { get; private set; }

    // The Connect button. With a character list source the account's characters are asked for first
    // (a short connection of its own, before the join): none (no accounts on the server, a new account,
    // no answer) connects with the typed name as before; some open the picker (PickerCharacters)
    // until ChooseCharacter, CancelPicker or BeginConnect. Without a source this is BeginConnect.
    public void BeginConnectWithPicker(ClientConnectionOptions options)
    {
        if (_characterList == null)
        {
            BeginConnect(options);
            return;
        }

        if (IsBusy)
        {
            return;
        }

        ClearPicker();
        if (!TryResolveRemoteOptions(options, out RemoteClientOptions? resolved))
        {
            return;
        }

        State = ConnectionFlowState.Connecting;
        StatusMessage = "Looking up your characters...";
        _pendingConnectionTask = ListThenConnectAsync(options, resolved!);
    }

    // Connects as the picked character (or a new name typed in the picker).
    public void ChooseCharacter(ClientConnectionOptions options, string characterName)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(characterName))
        {
            return;
        }

        BeginConnect(options with { PlayerName = characterName.Trim() });
    }

    public void CancelPicker()
    {
        if (PickerCharacters == null || IsBusy)
        {
            return;
        }

        ClearPicker();
        State = ConnectionFlowState.ReadyToConnect;
        StatusMessage = "Connect when ready.";
    }

    private void ClearPicker() => PickerCharacters = null;

    private async Task ListThenConnectAsync(ClientConnectionOptions options, RemoteClientOptions resolved)
    {
        IReadOnlyList<CharacterListEntry> characters;
        try
        {
            characters = await _characterList!.FetchAsync(resolved);
        }
        catch (Exception)
        {
            characters = [];
        }

        if (characters.Count == 0)
        {
            StatusMessage = $"Connecting to {resolved.Host}:{resolved.Port}...";
            await ConnectInternalAsync(resolved);
            return;
        }

        PickerCharacters = characters;
        State = ConnectionFlowState.ReadyToConnect;
        StatusMessage = "Choose a character, or enter a new name and connect.";
    }

    // A remote connect never reads the game's content files (the server sends everything the
    // client draws), so a content error does not block it. Only the local server needs them. On
    // failure the state and message say why and the pending task is completed.
    private bool TryResolveRemoteOptions(ClientConnectionOptions options, out RemoteClientOptions? resolved)
    {
        if (!options.TryCreateRemoteOptions(out resolved, out string? error))
        {
            State = ConnectionFlowState.Failed;
            StatusMessage = error ?? "Connection settings are invalid.";
            _pendingConnectionTask = Task.CompletedTask;
            return false;
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
                return false;
            }
        }

        return true;
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

    // "local" for every loopback address and port (the integrated server on whatever port it gets, a
    // dedicated server on this computer, another window's server), else host:port. One person keeps one
    // account across them.
    private string ServerKeyFor(RemoteClientOptions options) =>
        ClientIdentityStore.ServerKeyFor(options.Host, options.Port, IsLoopbackHost(options.Host));

    private static bool IsLoopbackHost(string host) => LoopbackHost.IsLoopback(host);
}
