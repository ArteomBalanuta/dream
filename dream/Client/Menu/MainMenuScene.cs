using Dream.Client.Networking;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Menu;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;

namespace Dream.Client.Menu;

// The title screen at launch and after a play session ends. Single Player and the screen navigation
// (Options, Key Bindings, Credits) are handled here; Join Server and Exit are returned to Game.
public sealed class MainMenuScene
{
    private readonly SinglePlayerLauncher _launcher;
    private readonly IReadOnlyList<MenuOptionRow> _optionRows;
    private readonly MenuScreenState _screen = new();
    // Why the last session ended, or a failed start; shown under the buttons until the player acts.
    private string? _notice;
    private bool _noticeIsError;

    public MainMenuScene(SinglePlayerLauncher launcher, IReadOnlyList<MenuOptionRow> optionRows, string? sessionEndReason = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _optionRows = optionRows ?? throw new ArgumentNullException(nameof(optionRows));
        if (!string.IsNullOrWhiteSpace(sessionEndReason))
        {
            _notice = $"Session ended: {sessionEndReason}";
            _noticeIsError = true;
        }
    }

    public CursorState DesiredCursorState => CursorState.Normal;

    public MenuScreen Screen => _screen.Current;

    public SinglePlayerLauncher Launcher => _launcher;

    public void Update() => _launcher.Update();

    public bool TryTakeConnectedSession(out RemoteClientSession? session) => _launcher.TryTakeConnectedSession(out session);

    // Draws the current screen. Returns JoinServer or Exit when the player chose them, else None. Never
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

        if (_screen.Apply(action))
        {
            return MenuAction.None;
        }

        switch (action)
        {
            case MenuAction.SinglePlayer:
                _notice = null;
                _launcher.RequestStart();
                return MenuAction.None;
            case MenuAction.JoinServer:
            case MenuAction.Exit:
                return action;
            default:
                return MenuAction.None;
        }
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
            default:
                string? status = _launcher.IsBusy || _launcher.Failed ? _launcher.StatusMessage : _notice;
                bool isError = _launcher.Failed || (!_launcher.IsBusy && _noticeIsError);
                return MainMenuPanel.Build(w, _launcher.IsBusy, status, isError);
        }
    }
}
