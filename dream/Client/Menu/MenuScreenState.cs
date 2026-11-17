using Dream.Rendering.Ui.Menu;

namespace Dream.Client.Menu;

// Which screen of a menu is showing. Options and Key Bindings are reached from the root and go back to
// their parent; Credits and Options both return to the root.
public sealed class MenuScreenState
{
    public MenuScreen Current { get; private set; } = MenuScreen.Root;

    public void Reset() => Current = MenuScreen.Root;

    // Applies a navigation action; returns true when it changed the screen.
    public bool Apply(MenuAction action)
    {
        MenuScreen next = (Current, action) switch
        {
            (MenuScreen.Root, MenuAction.Options) => MenuScreen.Options,
            (MenuScreen.Root, MenuAction.Credits) => MenuScreen.Credits,
            (MenuScreen.Root, MenuAction.Multiplayer) => MenuScreen.Multiplayer,
            (MenuScreen.Multiplayer, MenuAction.Back) => MenuScreen.Root,
            (MenuScreen.Options, MenuAction.KeyBindings) => MenuScreen.KeyBindings,
            (MenuScreen.KeyBindings, MenuAction.Back) => MenuScreen.Options,
            (MenuScreen.Options, MenuAction.Back) => MenuScreen.Root,
            (MenuScreen.Credits, MenuAction.Back) => MenuScreen.Root,
            _ => Current
        };
        bool changed = next != Current;
        Current = next;
        return changed;
    }
}
