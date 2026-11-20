using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Dream.Assets;
using Dream.Client.Menu;
using Dream.Client.Networking;
using Dream.Rendering.Core;

namespace Dream.Client.Startup;


public sealed class StartupConnectionScene : IDisposable
{
    private const string AutoConnectEnvironmentVariable = "DREAM_AUTO_CONNECT";
    private readonly StartupMenuRenderer _renderer;
    private readonly NetworkSessionCoordinator _coordinator;
    private readonly Action<ClientConnectionOptions>? _persistConnectionOptions;
    private readonly bool _autoConnectUsesInitialOptions;
    private string _host = string.Empty;
    private string _portText = string.Empty;
    private string _playerName = "dream-client";
    private bool _appliedLocalDefaults;
    private bool _autoConnectAttempted;
    private bool _disposed;
    private readonly bool _allowBack;
    private bool _backRequested;

    public StartupConnectionScene(
        Vector2i initialSize,
        NetworkSessionCoordinator coordinator,
        ClientConnectionOptions? initialOptions = null,
        Action<ClientConnectionOptions>? persistConnectionOptions = null,
        bool autoConnectUsesInitialOptions = false,
        string? sessionEndReason = null,
        bool allowBack = false)
    {
        _allowBack = allowBack;
        _renderer = new StartupMenuRenderer(initialSize);
        _coordinator = coordinator;
        _persistConnectionOptions = persistConnectionOptions;
        _autoConnectUsesInitialOptions = autoConnectUsesInitialOptions;
        // A scene Game returns to after a session ended shows why. DREAM_AUTO_CONNECT applies to the first
        // scene only. A lost connection is retried below, but only when the session had lasted
        // NetworkSessionCoordinator.AutoReconnectMinimumSession; otherwise the scene waits for the player,
        // so a server that keeps dropping the client cannot cause a reconnect loop.
        _autoConnectAttempted = sessionEndReason != null;
        if (initialOptions != null)
        {
            _host = initialOptions.Host;
            _portText = initialOptions.PortText;
            _playerName = initialOptions.PlayerName;
            _appliedLocalDefaults = true;
        }
        else
        {
            TryApplyLocalDefaults();
        }

        if (sessionEndReason != null)
        {
            _coordinator.ReportSessionEnded(sessionEndReason);
            // A lost connection (not a kick, a ban or a refused join) of a session that lasted long enough is
            // retried. When the ended session was on this game's integrated server the coordinator's
            // connector relaunches it instead, and CanAutoReconnect is false. The session is judged even when
            // the reason is not retryable, so its stamp is cleared.
            bool lastedLongEnough = _coordinator.TakeSessionLastedLongEnoughToReconnect();
            if (lastedLongEnough && RemoteClientSession.IsRetryableEnd(sessionEndReason) && _coordinator.CanAutoReconnect)
            {
                _coordinator.BeginReconnect(new ClientConnectionOptions(_host, _portText, _playerName));
            }
        }
    }

    // The sprite cursor replaces the OS one while it is drawn and the window is focused.
    public CursorState DesiredCursorState => DesiredCursorStateFor(_windowFocused);

    // Decided from the live focus and whether the cursor sprite loaded, not from what the last frame drew,
    // so the first focused frame does not show the OS arrow beside the sprite.
    public CursorState DesiredCursorStateFor(bool windowFocused) =>
        _renderer.SpriteCursorAvailable && windowFocused ? CursorState.Hidden : CursorState.Normal;

    private bool _windowFocused = true;

    public void Update()
    {
        TryApplyLocalDefaults();
        TryAutoConnect();
    }

    public void Render(
        FrameEventArgs args,
        bool windowFocused,
        MouseState mouseState,
        KeyboardState keyboardState,
        float uiScaleSetting = 0f)
    {
        _windowFocused = windowFocused;
        _renderer.UiScaleSetting = uiScaleSetting;
        string previousHost = _host;
        string previousPort = _portText;
        string previousPlayerName = _playerName;

        // Esc ends the automatic reconnect.
        if (_coordinator.IsReconnecting && keyboardState.IsKeyPressed(Keys.Escape))
        {
            _coordinator.StopReconnect();
        }

        GL.ClearColor(0.08f, 0.09f, 0.12f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        ConnectionUiRequest request = _renderer.DrawFrame(
            args,
            windowFocused,
            mouseState,
            keyboardState,
            _host,
            _portText,
            _playerName,
            _coordinator.LocalServerStatus,
            _coordinator.StatusMessage,
            _coordinator.IsBusy,
            _coordinator.PickerCharacters,
            out string? pickedCharacter,
            out bool cancelPickerPressed,
            _allowBack);

        if (cancelPickerPressed
            || !string.Equals(previousHost, request.Host, StringComparison.Ordinal)
            || !string.Equals(previousPort, request.PortText, StringComparison.Ordinal))
        {
            // The list belongs to the endpoint it was asked from.
            _coordinator.CancelPicker();
        }

        _backRequested |= request.BackPressed;
        _host = request.Host;
        _portText = request.PortText;
        _playerName = request.PlayerName;

        if (request.UseLocalServerPressed
            && _coordinator.TryUseLocalServer(
                string.IsNullOrWhiteSpace(_playerName) ? "dream-client" : _playerName,
                out ClientConnectionOptions? localDefaults))
        {
            // Only a ready server changes the fields; otherwise the status line says why and the
            // entered endpoint stays (and is not persisted).
            _host = localDefaults!.Host;
            _portText = localDefaults.PortText;
            _playerName = localDefaults.PlayerName;
            _appliedLocalDefaults = true;
        }

        PersistConnectionOptionsIfChanged(previousHost, previousPort, previousPlayerName);

        if (pickedCharacter != null)
        {
            _playerName = pickedCharacter;
            ClientConnectionOptions picked = new(_host, _portText, _playerName);
            _coordinator.ChooseCharacter(picked, pickedCharacter);
            _persistConnectionOptions?.Invoke(picked);
        }
        else if (request.ConnectPressed)
        {
            _coordinator.BeginConnectWithPicker(new ClientConnectionOptions(_host, _portText, _playerName));
        }
    }

    // True once, after the player pressed Back (only offered when the scene was created with allowBack).
    public bool TryTakeBackRequest()
    {
        bool requested = _backRequested;
        _backRequested = false;
        return requested;
    }

    public void Resize(Vector2i size)
    {
        _renderer.Resize(size);
    }

    public bool TryTakeConnectedSession(out RemoteClientSession? session)
    {
        return _coordinator.TryTakeConnectedSession(out session);
    }

    public void QueueTextInput(uint unicode)
    {
        _renderer.QueueTextInput(unicode);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderer.Dispose();
    }

    private void TryApplyLocalDefaults()
    {
        if (_appliedLocalDefaults)
        {
            return;
        }

        ClientConnectionOptions localDefaults = _coordinator.UseLocalServerDefaults(_playerName);
        _host = localDefaults.Host;
        _portText = localDefaults.PortText;
        _playerName = localDefaults.PlayerName;
        _appliedLocalDefaults = true;
    }

    private void TryAutoConnect()
    {
        if (_autoConnectAttempted || !IsAutoConnectEnabled(Environment.GetEnvironmentVariable, localServerPath: !_autoConnectUsesInitialOptions))
        {
            return;
        }

        if (_autoConnectUsesInitialOptions)
        {
            _autoConnectAttempted = true;
            ClientConnectionOptions configuredOptions = new(_host, _portText, _playerName);
            _coordinator.BeginConnect(configuredOptions);
            _persistConnectionOptions?.Invoke(configuredOptions);
            return;
        }

        _coordinator.EnsureLocalServerStarted();
        if (!_coordinator.IsLocalServerReady)
        {
            return;
        }

        ClientConnectionOptions localDefaults = _coordinator.UseLocalServerDefaults(
            string.IsNullOrWhiteSpace(_playerName) ? "dream-client" : _playerName);
        _host = localDefaults.Host;
        _portText = localDefaults.PortText;
        _playerName = localDefaults.PlayerName;
        _autoConnectAttempted = true;
        _coordinator.BeginConnect(localDefaults);
        _persistConnectionOptions?.Invoke(localDefaults);
    }

    // A capture run has no one to press Connect, so DREAM_HUD_CAPTURE implies auto-connect, but only on
    // the local/integrated path: a leftover capture variable must never join a remote server unasked.
    internal static bool IsAutoConnectEnabled(Func<string, string?> env, bool localServerPath = true)
    {
        if (localServerPath && !string.IsNullOrWhiteSpace(env(MenuLaunchPolicy.HudCaptureEnvironmentVariable)))
        {
            return true;
        }

        string? value = env(AutoConnectEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private void PersistConnectionOptionsIfChanged(string previousHost, string previousPort, string previousPlayerName)
    {
        if (_persistConnectionOptions == null)
        {
            return;
        }

        if (string.Equals(previousHost, _host, StringComparison.Ordinal)
            && string.Equals(previousPort, _portText, StringComparison.Ordinal)
            && string.Equals(previousPlayerName, _playerName, StringComparison.Ordinal))
        {
            return;
        }

        _persistConnectionOptions(new ClientConnectionOptions(_host, _portText, _playerName));
    }
}
