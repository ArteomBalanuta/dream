namespace Dream.Rendering.Ui.Menu;

// What a front-end panel asks for this frame. Panels never act themselves; the scene or Game does.
public enum MenuAction : byte
{
    None,
    SinglePlayer,
    Multiplayer,
    Options,
    Credits,
    Exit,
    Resume,
    DeveloperSettings,
    ExitToMainMenu,
    ExitGame,
    KeyBindings,
    Camera,
    Back,
    Join,
    Host
}

// Which screen of a menu is showing. The main menu and the pause menu share Options, KeyBindings and Camera.
public enum MenuScreen : byte
{
    Root,
    Options,
    KeyBindings,
    Camera,
    Credits,
    Multiplayer
}

// Keys the front end reacts to, free of OpenTK so the panels and the scene test without a window.
public enum MenuKey : byte
{
    Backspace,
    Tab,
    Enter,
    Escape,
    Delete,
    Left,
    Right,
    Home,
    End,
    // Ctrl+A, Ctrl+C, Ctrl+X, Ctrl+V: the window maps the shortcuts, so the scene stays layout-free.
    SelectAll,
    Copy,
    Cut,
    Paste
}

// Shift extends a selection (and reverses Tab); Control moves by word.
[Flags]
public enum MenuKeyModifiers : byte
{
    None = 0,
    Shift = 1,
    Control = 2
}

public enum MenuOptionKind : byte
{
    Header,
    Slider,
    Toggle
}

// One line of the Options screen. Game builds the list (MenuOptionRows) so a later feature can add its
// own lines (the audio sliders) without touching the panels. A slider calls SetValue while it is
// dragged and Commit once on release; a toggle calls SetToggle and then Commit at once. Commit is where
// a row persists its setting.
public sealed class MenuOptionRow
{
    private MenuOptionRow(string label, MenuOptionKind kind)
    {
        Label = label;
        Kind = kind;
    }

    public string Label { get; }

    public MenuOptionKind Kind { get; }

    public float Min { get; private init; }

    public float Max { get; private init; }

    // Slider values snap to multiples of Step from Min. 0 means continuous.
    public float Step { get; private init; }

    public Func<float>? GetValue { get; private init; }

    public Action<float>? SetValue { get; private init; }

    public Func<float, string>? Format { get; private init; }

    public Func<bool>? GetToggle { get; private init; }

    public Action<bool>? SetToggle { get; private init; }

    public Action? Commit { get; private init; }

    // A disabled row is drawn dimmed and ignores the pointer. Null is enabled.
    public Func<bool>? IsEnabled { get; private init; }

    public bool Enabled => IsEnabled?.Invoke() ?? true;

    public static MenuOptionRow Header(string label) => new(label, MenuOptionKind.Header);

    public static MenuOptionRow Slider(
        string label,
        float min,
        float max,
        float step,
        Func<float> get,
        Action<float> set,
        Func<float, string>? format = null,
        Action? commit = null,
        Func<bool>? isEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        return new MenuOptionRow(label, MenuOptionKind.Slider)
        {
            Min = min,
            Max = max > min ? max : min + 1f,
            Step = step,
            GetValue = get,
            SetValue = set,
            Format = format,
            Commit = commit,
            IsEnabled = isEnabled
        };
    }

    public static MenuOptionRow Toggle(
        string label,
        Func<bool> get,
        Action<bool> set,
        Action? commit = null,
        Func<bool>? isEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        return new MenuOptionRow(label, MenuOptionKind.Toggle)
        {
            GetToggle = get,
            SetToggle = set,
            Commit = commit,
            IsEnabled = isEnabled
        };
    }

    // The slider position 0..1 for a value, and back (snapped and clamped).
    public float ToUnit(float value) => Math.Clamp((value - Min) / (Max - Min), 0f, 1f);

    public float FromUnit(float unit)
    {
        float v = Min + Math.Clamp(unit, 0f, 1f) * (Max - Min);
        if (Step > 0f)
        {
            v = Min + MathF.Round((v - Min) / Step) * Step;
        }

        return Math.Clamp(v, Min, Max);
    }

    public string FormatValue(float value) =>
        Format?.Invoke(value) ?? value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
