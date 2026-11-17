using Dream.Client.Networking;
using Dream.Client.Startup;

namespace Dream.Client.Menu;

public enum SinglePlayerPhase : byte
{
    Idle,
    // Requested; one frame with the status line is shown before the blocking server start.
    Waking,
    // The integrated server is starting (or waiting for the previous one to finish saving).
    Starting,
    Connecting,
    Connected,
    Failed
}

// Single Player: starts the integrated server, then connects to it, the same local path the launch
// used to take (EnsureLocalServerStarted -> ready -> UseLocalServerDefaults -> BeginConnect). The
// window drives it: RequestStart on the click, Update every update frame, NotifyFramePresented after
// each rendered frame (the server start blocks the update thread, so a frame with the status must
// have been drawn first).
public sealed class SinglePlayerLauncher
{
    public const string WakingMessage = "Waking the world...";
    public const string StartingMessage = "Starting the local server...";
    public const string ServerBusyMessage = "The local server is still starting; try again in a moment.";

    // Frames that must be presented in the Waking phase before the blocking start.
    private const int FramesBeforeStart = 2;

    private readonly NetworkSessionCoordinator _coordinator;
    private readonly ILocalServerHandle _localServer;
    private readonly string? _contentError;
    private readonly Func<string> _playerName;
    private int _wakingFrames;
    private string? _hostPlayerName;
    // True while the request in flight is a Join. Only a Join is cancellable: a Single Player or Host
    // start has an integrated server mid-start behind it, which Escape must not leave half-torn-down.
    private bool _joining;

    public SinglePlayerLauncher(
        NetworkSessionCoordinator coordinator,
        ILocalServerHandle localServer,
        string? contentError,
        Func<string> playerName)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _localServer = localServer ?? throw new ArgumentNullException(nameof(localServer));
        _contentError = contentError;
        _playerName = playerName ?? throw new ArgumentNullException(nameof(playerName));
    }

    public SinglePlayerPhase Phase { get; private set; }

    public string StatusMessage { get; private set; } = string.Empty;

    public bool Failed => Phase == SinglePlayerPhase.Failed;

    // True from the click until the session is taken or the start failed: the menu disables its buttons.
    public bool IsBusy => Phase is SinglePlayerPhase.Waking or SinglePlayerPhase.Starting or SinglePlayerPhase.Connecting;

    // Starts (or retries after a failure). Ignored while a start is already running.
    public void RequestStart()
    {
        if (IsBusy || Phase == SinglePlayerPhase.Connected)
        {
            return;
        }

        if (_contentError != null)
        {
            Fail(_contentError);
            return;
        }

        if (_localServer.ErrorMessage != null)
        {
            _localServer.ResetFailure();
        }

        if (!_localServer.TryConfigureListen(LocalServerListenOptions.Loopback))
        {
            Fail(ServerBusyMessage);
            return;
        }

        _hostPlayerName = null;
        _joining = false;
        _wakingFrames = 0;
        StatusMessage = WakingMessage;
        Phase = SinglePlayerPhase.Waking;
    }

    // Host: starts the integrated server on every interface at port, then joins it over loopback as
    // playerName, through the same Waking -> Starting -> Connecting path as RequestStart.
    public void RequestHost(int port, string playerName)
    {
        if (IsBusy || Phase == SinglePlayerPhase.Connected)
        {
            return;
        }

        if (_contentError != null)
        {
            Fail(_contentError);
            return;
        }

        if (_localServer.ErrorMessage != null)
        {
            _localServer.ResetFailure();
        }

        LocalServerListenOptions hosted = LocalServerListenOptions.Hosted(port);
        if (!_localServer.TryConfigureListen(hosted))
        {
            Fail(ServerBusyMessage);
            return;
        }

        _hostPlayerName = playerName;
        _joining = false;
        _wakingFrames = 0;
        StatusMessage = WakingMessage;
        Phase = SinglePlayerPhase.Waking;
    }

    // Join: no local server and no content-error gate; straight to Connecting.
    public void RequestJoin(ClientConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (IsBusy || Phase == SinglePlayerPhase.Connected)
        {
            return;
        }

        if (!options.TryCreateRemoteOptions(out _, out string? error))
        {
            Fail(error ?? "Connection settings are invalid.");
            return;
        }

        _hostPlayerName = null;
        _joining = true;
        Phase = SinglePlayerPhase.Connecting;
        _coordinator.BeginConnect(options);
        StatusMessage = _coordinator.StatusMessage;
        PollConnecting();
    }

    // Escape on the loading panel of a Join: abandons the connect and goes back to the Multiplayer
    // screen at once, instead of waiting out the OS connect timeout or the 45 s join timeout. The
    // coordinator drops a session that still arrives, so a cancelled join can never attach. False
    // (nothing happened) when the request in flight is not a Join.
    public bool CancelConnect()
    {
        if (!_joining || Phase != SinglePlayerPhase.Connecting)
        {
            return false;
        }

        _joining = false;
        _coordinator.CancelConnect();
        StatusMessage = string.Empty;
        Phase = SinglePlayerPhase.Idle;
        return true;
    }

    // Clears a Failed phase back to Idle, so a stale failure is not shown on another screen.
    public void Dismiss()
    {
        if (Phase == SinglePlayerPhase.Failed)
        {
            Phase = SinglePlayerPhase.Idle;
            StatusMessage = string.Empty;
            if (_localServer.ListenOptions.AllowRemotePlayers)
            {
                _localServer.TryConfigureListen(LocalServerListenOptions.Loopback);
            }
        }
    }

    // Called by the scene after it presented a frame.
    public void NotifyFramePresented()
    {
        if (Phase == SinglePlayerPhase.Waking)
        {
            _wakingFrames++;
        }
    }

    // Update thread, once per update frame.
    public void Update()
    {
        switch (Phase)
        {
            case SinglePlayerPhase.Waking:
                if (_wakingFrames >= FramesBeforeStart)
                {
                    // Blocks while a new integrated server starts (unless it waits for the previous one).
                    _coordinator.EnsureLocalServerStarted();
                    StatusMessage = _hostPlayerName != null && _localServer.ListenOptions.AllowRemotePlayers
                        ? $"Hosting on port {_localServer.ListenOptions.Port}..."
                        : StartingMessage;
                    Phase = SinglePlayerPhase.Starting;
                    PollStarting();
                }

                break;
            case SinglePlayerPhase.Starting:
                PollStarting();
                break;
            case SinglePlayerPhase.Connecting:
                PollConnecting();
                break;
        }
    }

    public bool TryTakeConnectedSession(out RemoteClientSession? session)
    {
        bool taken = _coordinator.TryTakeConnectedSession(out session);
        if (taken)
        {
            Phase = SinglePlayerPhase.Connected;
        }

        return taken;
    }

    private void PollStarting()
    {
        if (Phase != SinglePlayerPhase.Starting)
        {
            return;
        }

        if (_localServer.ErrorMessage is { } error)
        {
            Fail($"Local server failed: {error}");
            return;
        }

        if (!_localServer.IsReady)
        {
            // Still starting, or waiting for the previous integrated server to finish saving.
            StatusMessage = _localServer.StartingStatus ?? StartingMessage;
            return;
        }

        string chosen = _hostPlayerName ?? _playerName();
        string name = string.IsNullOrWhiteSpace(chosen) ? "dream-client" : chosen;
        _coordinator.BeginConnect(_coordinator.UseLocalServerDefaults(name));
        StatusMessage = _coordinator.StatusMessage;
        Phase = SinglePlayerPhase.Connecting;
        PollConnecting();
    }

    private void PollConnecting()
    {
        switch (_coordinator.State)
        {
            case ConnectionFlowState.Connected:
                // The session waits in the coordinator until TryTakeConnectedSession.
                StatusMessage = _coordinator.StatusMessage;
                break;
            case ConnectionFlowState.Failed:
                Fail(_coordinator.StatusMessage);
                break;
            default:
                StatusMessage = _coordinator.StatusMessage;
                break;
        }
    }

    private void Fail(string message)
    {
        if (_hostPlayerName != null)
        {
            // A failed Host must not leave the world listening on every interface.
            _hostPlayerName = null;
            _localServer.TryConfigureListen(LocalServerListenOptions.Loopback);
        }

        StatusMessage = string.IsNullOrWhiteSpace(message) ? "Could not start a single player game." : message;
        Phase = SinglePlayerPhase.Failed;
    }
}
