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

    // Frames that must be presented in the Waking phase before the blocking start.
    private const int FramesBeforeStart = 2;

    private readonly NetworkSessionCoordinator _coordinator;
    private readonly ILocalServerHandle _localServer;
    private readonly string? _contentError;
    private readonly Func<string> _playerName;
    private int _wakingFrames;

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

        _wakingFrames = 0;
        StatusMessage = WakingMessage;
        Phase = SinglePlayerPhase.Waking;
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
                    StatusMessage = StartingMessage;
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

        string name = string.IsNullOrWhiteSpace(_playerName()) ? "dream-client" : _playerName();
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
        StatusMessage = string.IsNullOrWhiteSpace(message) ? "Could not start a single player game." : message;
        Phase = SinglePlayerPhase.Failed;
    }
}
