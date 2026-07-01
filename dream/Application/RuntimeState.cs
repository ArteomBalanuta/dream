using OpenTK.Windowing.Common;

namespace Dream.Application;


public enum RuntimeMode
{
    Gameplay,
    SettingsMenu
}

public readonly record struct RuntimeState(RuntimeMode Mode)
{
    public bool AllowGameplayInput => Mode == RuntimeMode.Gameplay;
    public CursorState DesiredCursorState => AllowGameplayInput ? CursorState.Grabbed : CursorState.Normal;
}
