using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Server;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Menu;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;

namespace Dream.Client.Menu;

// The title screen at launch and after a play session ends. Single Player and the screen navigation
// (Options, Key Bindings, Credits, Multiplayer) are handled here; Exit is returned to Game.
public sealed class MainMenuScene
{
    private readonly SinglePlayerLauncher _launcher;
    private readonly IReadOnlyList<MenuOptionRow> _optionRows;
    private readonly MenuScreenState _screen = new();
    // Why the last session ended, or a failed start; shown under the buttons until the player acts.
    private string? _notice;
    private bool _noticeIsError;
    private readonly MultiplayerFormState _form;
    private readonly Action<MultiplayerDefaults>? _persistMultiplayer;
    private MultiplayerDefaults _saved;
    private readonly IMenuClipboard? _clipboard;

    public MainMenuScene(
        SinglePlayerLauncher launcher,
        IReadOnlyList<MenuOptionRow> optionRows,
        string? sessionEndReason = null,
        MultiplayerDefaults? multiplayer = null,
        Action<MultiplayerDefaults>? persistMultiplayer = null,
        IMenuClipboard? clipboard = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _optionRows = optionRows ?? throw new ArgumentNullException(nameof(optionRows));
        _persistMultiplayer = persistMultiplayer;
        _clipboard = clipboard;
        _saved = multiplayer ?? new MultiplayerDefaults("127.0.0.1", ServerBootstrap.DefaultPort, ServerBootstrap.DefaultPort, "dream-client");
        _form = new MultiplayerFormState(_saved.PlayerName, _saved.Address, _saved.JoinPort, _saved.HostPort);
        if (!string.IsNullOrWhiteSpace(sessionEndReason))
        {
            _notice = $"Session ended: {sessionEndReason}";
            _noticeIsError = true;
        }
    }

    public CursorState DesiredCursorState => CursorState.Normal;

    public MenuScreen Screen => _screen.Current;

    public SinglePlayerLauncher Launcher => _launcher;

    internal MultiplayerFormState Form => _form;

    public void Update() => _launcher.Update();

    public bool TryTakeConnectedSession(out RemoteClientSession? session) => _launcher.TryTakeConnectedSession(out session);

    // A typed character, applied at once to the focused field (Multiplayer screen only).
    public void QueueTextInput(uint unicode)
    {
        if (_screen.Current == MenuScreen.Multiplayer && !_launcher.IsBusy)
        {
            _form.TypeCharacter(unicode);
        }
    }

    // A key press with its modifiers; the Multiplayer screen passes them and the clipboard to the form.
    // A key press. Returns what Render would return (None or Exit); never throws on Root.
    public MenuAction HandleKey(MenuKey key) => HandleKey(key, MenuKeyModifiers.None);

    public MenuAction HandleKey(MenuKey key, MenuKeyModifiers modifiers)
    {
        switch (_screen.Current)
        {
            case MenuScreen.Multiplayer:
                if (_launcher.IsBusy)
                {
                    return MenuAction.None;
                }

                return Handle(key == MenuKey.Escape ? MenuAction.Back : _form.HandleKey(key, modifiers, _clipboard));
            case MenuScreen.Options:
            case MenuScreen.Credits:
            case MenuScreen.KeyBindings:
                return key == MenuKey.Escape ? Handle(MenuAction.Back) : MenuAction.None;
            default:
                return MenuAction.None;
        }
    }

    // Draws the current screen. Returns Exit when the player chose them, else None. Never
    // draws and returns None while the renderer is not ready (the window shows a dark frame).
    public MenuAction Render(MenuUiRenderer renderer, int width, int height, float uiScaleSetting, UiPointer pointer)
    {
        GL.ClearColor(0.02f, 0.025f, 0.03f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        MenuAction action = MenuAction.None;
        bool drawn = renderer.Draw(width, height, uiScaleSetting, pointer, w => action = BuildScreen(w));
        if (drawn)
        {
            _launcher.NotifyFramePresented();
        }

        return Handle(action);
    }

    // The effect of one action; split from Render so tests can drive it without GL.
    internal MenuAction Handle(MenuAction action)
    {
        if (action == MenuAction.None)
        {
            return MenuAction.None;
        }

        if (_launcher.IsBusy && action is MenuAction.Back or MenuAction.Join or MenuAction.Host)
        {
            return MenuAction.None;
        }

        if (_screen.Apply(action))
        {
            _launcher.Dismiss();
            _notice = null;
            _noticeIsError = false;
            return MenuAction.None;
        }

        switch (action)
        {
            case MenuAction.SinglePlayer:
                _notice = null;
                _launcher.RequestStart();
                return MenuAction.None;
            case MenuAction.Join:
                if (_screen.Current == MenuScreen.Multiplayer)
                {
                    Join();
                }

                return MenuAction.None;
            case MenuAction.Host:
                if (_screen.Current == MenuScreen.Multiplayer)
                {
                    Host();
                }

                return MenuAction.None;
            case MenuAction.Exit:
                return action;
            default:
                return MenuAction.None;
        }
    }

    private void Join()
    {
        string address = _form.Address.Text.Trim();
        string portText = _form.JoinPort.Text;
        string name = _form.PlayerName.Text.Trim();
        ClientConnectionOptions options = new(address, portText, name);
        if (!options.TryCreateRemoteOptions(out _, out string? error))
        {
            _notice = error;
            _noticeIsError = true;
            return;
        }

        _notice = null;
        _noticeIsError = false;
        LocalServerListenOptions.TryParsePort(portText, out int joinPort);
        int hostPort = LocalServerListenOptions.TryParsePort(_form.HostPort.Text, out int parsedHost) ? parsedHost : _saved.HostPort;
        Persist(new MultiplayerDefaults(address, joinPort, hostPort, name));
        _launcher.RequestJoin(options);
    }

    private void Host()
    {
        if (!LocalServerListenOptions.TryParsePort(_form.HostPort.Text, out int port))
        {
            _notice = "Port must be a valid integer between 1 and 65535.";
            _noticeIsError = true;
            return;
        }

        string name = _form.PlayerName.Text.Trim();
        if (name.Length == 0)
        {
            _notice = "Player name is required.";
            _noticeIsError = true;
            return;
        }

        _notice = null;
        _noticeIsError = false;
        int joinPort = LocalServerListenOptions.TryParsePort(_form.JoinPort.Text, out int parsedJoin) ? parsedJoin : _saved.JoinPort;
        Persist(new MultiplayerDefaults(_form.Address.Text.Trim(), joinPort, port, name));
        _launcher.RequestHost(port, name);
    }

    private void Persist(MultiplayerDefaults defaults)
    {
        _saved = defaults;
        _persistMultiplayer?.Invoke(defaults);
    }

    private string? Status(out bool isError)
    {
        isError = _launcher.Failed || (!_launcher.IsBusy && _noticeIsError);
        return _launcher.IsBusy || _launcher.Failed ? _launcher.StatusMessage : _notice;
    }

    private MenuAction BuildScreen(MenuWidgets w)
    {
        switch (_screen.Current)
        {
            case MenuScreen.Options:
                return OptionsPanel.Build(w, _optionRows, backdrop: true);
            case MenuScreen.KeyBindings:
                return KeyBindingsPanel.Build(w, backdrop: true);
            case MenuScreen.Credits:
                return MainMenuPanel.BuildCredits(w);
            case MenuScreen.Multiplayer:
                string? mpStatus = Status(out bool mpError);
                return MainMenuPanel.BuildMultiplayer(w, _form, _launcher.IsBusy, mpStatus, mpError);
            default:
                string? status = Status(out bool isError);
                return MainMenuPanel.Build(w, _launcher.IsBusy, status, isError);
        }
    }
}
