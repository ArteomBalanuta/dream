using OpenTK.Windowing.Common;

namespace Dream.Application;


public enum RuntimeMode
{
    Gameplay,
    InventoryOverlay,
    SettingsMenu
}

public readonly record struct RuntimeState(RuntimeMode Mode)
{
    // The inventory overlay no longer pauses play: clicks outside the windows still move the character.
    public bool AllowGameplayInput => Mode != RuntimeMode.SettingsMenu;

    // Click-to-move needs the cursor free at all times.
    public CursorState DesiredCursorState => CursorState.Normal;
}
