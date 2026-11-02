using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Menu;
using OpenTK.Mathematics;

namespace Dream.Client.Menu;

// DREAM_MENU_CAPTURE=<directory>: walks the title screen through its screens, writes a PNG of each and
// asks the window to close. For reviewing the front-end art without clicking through it; nothing else
// uses it, and it only runs on the title screen.
public sealed class MenuCapture
{
    public const string EnvironmentVariable = "DREAM_MENU_CAPTURE";

    private readonly string _directory;
    private int _frame;

    private MenuCapture(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public static MenuCapture? FromEnvironment()
    {
        string? dir = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(dir) ? null : new MenuCapture(dir.Trim());
    }

    // True when the whole walk is done and the window should close.
    public bool Done { get; private set; }

    // A scripted pointer for the frames that need one (hovering the second button), else null.
    public UiPointer? ScriptedPointer(int width, int height, float uiScale)
    {
        if (_frame is >= 44 and <= 56)
        {
            UiLayout layout = UiLayout.Create(width, height, uiScale);
            float originY = MathF.Max(0f, (height - MenuWidgets.DesignHeightUnits * layout.Scale) * 0.5f);
            float y = originY + (MainMenuPanel.ButtonsTopUnits + MenuWidgets.ButtonPitchUnits + MenuWidgets.ButtonHeightUnits * 0.5f) * layout.Scale;
            return new UiPointer(new Vector2(width * 0.5f, y), false, false, true);
        }

        return null;
    }

    // Called after the scene drew its frame, before the buffers swap.
    public void AfterFrame(MainMenuScene scene, MenuUiRenderer renderer, int width, int height, float uiScale, Action<string> capture)
    {
        _frame++;
        switch (_frame)
        {
            case 40:
                capture(Path.Combine(_directory, "menu-root.png"));
                break;
            case 54:
                capture(Path.Combine(_directory, "menu-hover.png"));
                break;
            case 60:
                scene.Handle(MenuAction.Options);
                break;
            case 70:
                capture(Path.Combine(_directory, "menu-options.png"));
                break;
            case 80:
                scene.Handle(MenuAction.KeyBindings);
                break;
            case 90:
                capture(Path.Combine(_directory, "menu-keys.png"));
                break;
            case 100:
                scene.Handle(MenuAction.Back);
                scene.Handle(MenuAction.Back);
                scene.Handle(MenuAction.Credits);
                break;
            case 110:
                capture(Path.Combine(_directory, "menu-credits.png"));
                break;
            case 120:
                scene.Handle(MenuAction.Back);
                break;
            case 130:
                // The pause menu drawn over the title screen's last frame.
                renderer.Draw(width, height, uiScale, new UiPointer(new Vector2(-1000f, -1000f), false, false, true), w => PauseMenuPanel.Build(w, true));
                capture(Path.Combine(_directory, "menu-pause.png"));
                Done = true;
                break;
        }
    }
}
