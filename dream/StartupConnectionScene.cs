using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

public sealed class StartupConnectionScene : IDisposable
{
    private readonly StartupMenuRenderer _renderer;
    private readonly NetworkSessionCoordinator _coordinator;
    private readonly Action<ClientConnectionOptions>? _persistConnectionOptions;
    private string _host = string.Empty;
    private string _portText = string.Empty;
    private string _playerName = "dream-client";
    private bool _appliedLocalDefaults;

    public StartupConnectionScene(
        Vector2i initialSize,
        NetworkSessionCoordinator coordinator,
        ClientConnectionOptions? initialOptions = null,
        Action<ClientConnectionOptions>? persistConnectionOptions = null)
    {
        _renderer = new StartupMenuRenderer(initialSize);
        _coordinator = coordinator;
        _persistConnectionOptions = persistConnectionOptions;
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
    }

    public CursorState DesiredCursorState => CursorState.Normal;

    public void Update()
    {
        TryApplyLocalDefaults();
    }

    public void Render(
        FrameEventArgs args,
        bool windowFocused,
        MouseState mouseState,
        KeyboardState keyboardState)
    {
        string previousHost = _host;
        string previousPort = _portText;
        string previousPlayerName = _playerName;

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
            _coordinator.IsBusy);

        _host = request.Host;
        _portText = request.PortText;
        _playerName = request.PlayerName;

        if (request.UseLocalServerPressed)
        {
            ClientConnectionOptions localDefaults = _coordinator.UseLocalServerDefaults(string.IsNullOrWhiteSpace(_playerName) ? "dream-client" : _playerName);
            _host = localDefaults.Host;
            _portText = localDefaults.PortText;
            _playerName = localDefaults.PlayerName;
            _appliedLocalDefaults = true;
        }

        PersistConnectionOptionsIfChanged(previousHost, previousPort, previousPlayerName);

        if (request.ConnectPressed)
        {
            _coordinator.BeginConnect(new ClientConnectionOptions(_host, _portText, _playerName));
        }
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
