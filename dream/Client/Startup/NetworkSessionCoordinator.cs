using Dream.Client.Identity;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Server;

namespace Dream.Client.Startup;

public sealed class NetworkSessionCoordinator
{
    // Copied onto the options of every connect (RemoteClientOptions.HoldSpawnProtection).
    public bool HoldSpawnProtection { get; init; }

    private readonly ILocalServerHandle _localServer;
    private readonly IRemoteSessionConnector _connector;
    private Task? _pendingConnectionTask;
    private RemoteClientSession? _connectedSession;
    // Bumped by CancelConnect. A connect task carries the generation it started with; once that no
    // longer matches it writes no state and hands out no session, so a join the player cancelled
    // cannot attach late when the server finally answers.
    private int _connectGeneration;

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
        StopReconnect();
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

    // ---- Reconnect after a lost connection (M8) ----
    //
    // A session that ended with "Connection lost: ..." is retried from the connection screen, as a full
    // rejoin (the scene was torn down, so nothing carries over, a party included). The waits are 1, 2, 4, 8
    // and 16 s, five attempts. A join rejection (a kick, a ban, a full server) ends it at once, and so do a
    // new Connect, Use Local Server and Esc.

    public const int MaxReconnectAttempts = 5;

    // Test seam: the wait before each attempt.
    internal static IReadOnlyList<TimeSpan> ReconnectDelays { get; set; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)];

    private CancellationTokenSource? _reconnect;

    public bool IsReconnecting => Volatile.Read(ref _reconnect) != null;

    // A session that ended on the integrated server relaunches it on connect instead; a retry there would
    // only start a second server. The connector tells (Game wires it; the scene cannot see Game's choice).
    public bool CanAutoReconnect => _connector.GetType().Name != "LocalServerRelaunchConnector";

    // Starts the retries for the endpoint the ended session used. Does nothing while a connect is in
    // flight, while retrying already, or when the connector relaunches a local server.
    public void BeginReconnect(ClientConnectionOptions options)
    {
        if (IsBusy || IsReconnecting || !CanAutoReconnect)
        {
            return;
        }

        ClearPicker();
        CancellationTokenSource cancellation = new();
        _reconnect = cancellation;
        _pendingConnectionTask = ReconnectAsync(options, cancellation, Volatile.Read(ref _connectGeneration));
    }

    // Ends the retries: no further attempt starts, and an attempt already in flight is cancelled. Should it
    // still come back with a live session, that session is closed rather than handed to the game.
    public void StopReconnect()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _reconnect, null);
        if (cancellation == null)
        {
            return;
        }

        cancellation.Cancel();
        if (!IsBusy)
        {
            State = ConnectionFlowState.ReadyToConnect;
            StatusMessage = "Reconnect cancelled.";
        }
    }

    // Clears the screen a cancelled reconnect left on Connecting: StopReconnect cannot do it itself while an
    // attempt is in flight (IsBusy is true), and nothing else can take the flow over while it stays
    // Connecting. CancelConnect does reset it, and moves the generation on, so this leaves it alone then.
    private void ResetCancelledReconnect(int generation)
    {
        if (IsCurrentConnect(generation) && State == ConnectionFlowState.Connecting)
        {
            State = ConnectionFlowState.ReadyToConnect;
            StatusMessage = "Reconnect cancelled.";
        }
    }

    private async Task ReconnectAsync(ClientConnectionOptions options, CancellationTokenSource cancellation, int generation)
    {
        string lastReason = "the server did not answer";
        try
        {
            IReadOnlyList<TimeSpan> delays = ReconnectDelays;
            int attempts = Math.Min(MaxReconnectAttempts, delays.Count);
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                State = ConnectionFlowState.ReadyToConnect;
                StatusMessage = $"Reconnecting ({attempt}/{attempts})...";
                try
                {
                    await Task.Delay(delays[attempt - 1], cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                if (!TryResolveRemoteOptions(options, out RemoteClientOptions? resolved))
                {
                    return;
                }

                State = ConnectionFlowState.Connecting;
                StatusMessage = $"Reconnecting ({attempt}/{attempts})...";
                try
                {
                    RemoteClientSession session = await _connector.ConnectAsync(
                        resolved! with { HoldSpawnProtection = HoldSpawnProtection },
                        cancellation.Token);
                    if (cancellation.IsCancellationRequested || !IsCurrentConnect(generation))
                    {
                        // Cancelled while this attempt was in flight (Esc, a new connect, Use Local Server).
                        // Close the session that arrived instead of publishing it: the player abandoned this
                        // reconnect and must not be dropped into the world a frame later.
                        DisposeInBackground(session);
                        ResetCancelledReconnect(generation);
                        return;
                    }

                    if (session.IsConnected || session.DisconnectReason is not string reason)
                    {
                        if (!TryPublish(session, generation))
                        {
                            ResetCancelledReconnect(generation);
                            return;
                        }

                        State = ConnectionFlowState.Connected;
                        StatusMessage = $"Connected to {resolved!.Host}:{resolved.Port}.";
                        return;
                    }

                    await session.DisposeAsync();
                    if (!RemoteClientSession.IsRetryableEnd(reason))
                    {
                        // The server answered and said no: kicked, banned, full, a name taken.
                        State = ConnectionFlowState.Failed;
                        StatusMessage = reason;
                        return;
                    }

                    lastReason = reason;
                }
                catch (ProtocolVersionMismatchException ex)
                {
                    State = ConnectionFlowState.Failed;
                    StatusMessage = ex.Message;
                    return;
                }
                catch (Exception ex)
                {
                    lastReason = ex.Message;
                }

                if (cancellation.IsCancellationRequested)
                {
                    ResetCancelledReconnect(generation);
                    return;
                }
            }

            State = ConnectionFlowState.Failed;
            StatusMessage = $"Could not reconnect: {lastReason}";
        }
        finally
        {
            Interlocked.CompareExchange(ref _reconnect, null, cancellation);
        }
    }

    public void BeginConnect(ClientConnectionOptions options)
    {
        StopReconnect();
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
        _pendingConnectionTask = ConnectInternalAsync(resolved, Volatile.Read(ref _connectGeneration));
    }

    // Abandons a connect in flight (the player left the Join screen). The flow is idle again at once,
    // whatever the socket is still doing: the attempt's generation is stale from here, so when it
    // settles it disposes its session instead of publishing it and leaves State and StatusMessage
    // alone. False when nothing was in flight.
    public bool CancelConnect()
    {
        StopReconnect();
        if (!IsBusy)
        {
            return false;
        }

        Interlocked.Increment(ref _connectGeneration);
        ClearPicker();
        DisposeInBackground(Interlocked.Exchange(ref _connectedSession, null));
        State = ConnectionFlowState.ReadyToConnect;
        StatusMessage = "Connect when ready.";
        return true;
    }

    private bool IsCurrentConnect(int generation) => Volatile.Read(ref _connectGeneration) == generation;

    // Hands the arrived session to the game, or disposes it. False means it was not published.
    //
    // Checking the generation and THEN assigning _connectedSession loses the race it was written to
    // win: a connect settles on a thread-pool thread (OpenTK's loop installs no synchronization
    // context), CancelConnect runs on the render thread, and a cancel landing between the check and
    // the assignment used to be overwritten - the player was dropped into the join they pressed
    // Escape on, with nothing left to dispose it later.
    //
    // So the slot is CLAIMED first and the generation re-tested after. CancelConnect bumps the
    // generation before it clears the slot, so whichever side of the claim a cancel falls on, one of
    // the two sees it: a cancel before the claim leaves the generation stale here (we take the
    // session back out and dispose it), and a cancel after the claim takes the session out itself
    // and disposes it (our take-back then fails, so it is disposed exactly once). The claim is a
    // compare-exchange against null, so a session published by another connect is never displaced.
    private bool TryPublish(RemoteClientSession session, int generation)
    {
        if (Interlocked.CompareExchange(ref _connectedSession, session, null) != null)
        {
            // Another connect already published; this one is stale by definition.
            DisposeInBackground(session);
            return false;
        }

        if (IsCurrentConnect(generation))
        {
            return true;
        }

        if (ReferenceEquals(Interlocked.CompareExchange(ref _connectedSession, null, session), session))
        {
            DisposeInBackground(session);
        }

        return false;
    }

    private static void DisposeInBackground(RemoteClientSession? session)
    {
        if (session == null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[startup] cancelled session not closed cleanly: {ex.Message}");
            }
        });
    }

    // The characters the picker shows (M1-C), set while the picker is open (State is ReadyToConnect); null otherwise.
    public IReadOnlyList<CharacterListEntry>? PickerCharacters { get; private set; }

    // The Connect button. With a character list source the account's characters are asked for first
    // (a short connection of its own, before the join): none (no accounts on the server, a new account,
    // no answer) connects with the typed name as before; some open the picker (PickerCharacters)
    // until ChooseCharacter, CancelPicker or BeginConnect. Without a source this is BeginConnect.
    public void BeginConnectWithPicker(ClientConnectionOptions options)
    {
        StopReconnect();
        if (_characterList == null)
        {
            BeginConnect(options);
            return;
        }

        if (IsBusy)
        {
            return;
        }

        // Connect with the picker open connects as the typed name (a new character); it does not
        // fetch the list again. Cancel closes the picker, so the next Connect fetches afresh.
        if (PickerCharacters != null && !string.IsNullOrWhiteSpace(options.PlayerName))
        {
            ChooseCharacter(options, options.PlayerName);
            return;
        }

        ClearPicker();
        if (!TryResolveRemoteOptions(options, out RemoteClientOptions? resolved))
        {
            return;
        }

        State = ConnectionFlowState.Connecting;
        StatusMessage = "Looking up your characters...";
        _pendingConnectionTask = ListThenConnectAsync(options, resolved!, Volatile.Read(ref _connectGeneration));
    }

    // Connects as the picked character (or a new name typed in the picker).
    public void ChooseCharacter(ClientConnectionOptions options, string characterName)
    {
        StopReconnect();
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

    private async Task ListThenConnectAsync(ClientConnectionOptions options, RemoteClientOptions resolved, int generation)
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

        if (!IsCurrentConnect(generation))
        {
            return;
        }

        if (characters.Count == 0)
        {
            StatusMessage = $"Connecting to {resolved.Host}:{resolved.Port}...";
            await ConnectInternalAsync(resolved, generation, afterProbe: true);
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
        if (session != null)
        {
            MarkSessionStarted(TimeProvider.System.GetTimestamp());
        }

        return session != null;
    }

    // A lost connection is retried automatically only when the session before it lasted at least this long,
    // so a server that accepts the client and drops it again at once cannot cause a reconnect loop.
    public static readonly TimeSpan AutoReconnectMinimumSession = TimeSpan.FromSeconds(30);

    // Each ended session builds a fresh scene and coordinator, so the start of the play session is kept
    // here, stamped when the game takes the connected session. 0: no session has been taken.
    private static long s_sessionStartedTimestamp;

    internal static void MarkSessionStarted(long timestamp) => Volatile.Write(ref s_sessionStartedTimestamp, timestamp);

    // Whether the play session that just ended lasted AutoReconnectMinimumSession or more. Reads and
    // clears the stamp, so a session is judged once.
    public bool TakeSessionLastedLongEnoughToReconnect()
    {
        long started = Interlocked.Exchange(ref s_sessionStartedTimestamp, 0);
        return started != 0
            && TimeProvider.System.GetElapsedTime(started) >= AutoReconnectMinimumSession;
    }

    // The list probe's connection may still hold its server slot when the real connect starts, so a
    // "Server is full." right after a probe is retried once after a short wait.
    internal static TimeSpan ProbeSlotRetryDelay { get; set; } = TimeSpan.FromMilliseconds(750);

    private async Task ConnectInternalAsync(RemoteClientOptions options, int generation, bool afterProbe = false)
    {
        bool retried = !afterProbe;
        while (true)
        {
            string? serverFullReason = await TryConnectOnceAsync(options, generation, retryOnServerFull: !retried);
            if (serverFullReason == null || !IsCurrentConnect(generation))
            {
                return;
            }

            retried = true;
            StatusMessage = $"Connecting to {options.Host}:{options.Port}...";
            await Task.Delay(ProbeSlotRetryDelay);
        }
    }

    // Returns the server-full reason when the caller should retry; null when the attempt is settled.
    private async Task<string?> TryConnectOnceAsync(RemoteClientOptions options, int generation, bool retryOnServerFull)
    {
        try
        {
            RemoteClientSession session = await _connector.ConnectAsync(options with { HoldSpawnProtection = HoldSpawnProtection });
            if (!IsCurrentConnect(generation))
            {
                // Cancelled while this connect was in flight: close whatever arrived and say nothing.
                DisposeInBackground(session);
                return null;
            }

            if (!session.IsConnected && session.DisconnectReason is string reason)
            {
                // Ended before the play scene could take it, e.g. a join rejected during the
                // connect: stay on the connection screen with the reason.
                await session.DisposeAsync();
                if (retryOnServerFull && IsServerFull(reason))
                {
                    return reason;
                }

                State = ConnectionFlowState.Failed;
                StatusMessage = reason;
                return null;
            }

            if (!TryPublish(session, generation))
            {
                return null;
            }

            State = ConnectionFlowState.Connected;
            StatusMessage = $"Connected to {options.Host}:{options.Port}.";
        }
        catch (Exception ex)
        {
            if (!IsCurrentConnect(generation))
            {
                // Cancelled: a failure nobody is waiting for must not overwrite the idle screen.
                return null;
            }

            if (retryOnServerFull && IsServerFull(ex.Message))
            {
                return ex.Message;
            }

            State = ConnectionFlowState.Failed;
            StatusMessage = ex.Message;
        }

        return null;
    }

    private static bool IsServerFull(string reason) =>
        reason.Contains(ServerConnectionOptions.ServerFullRejectionReason, StringComparison.Ordinal);

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
