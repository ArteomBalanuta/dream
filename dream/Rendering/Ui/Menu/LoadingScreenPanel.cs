namespace Dream.Rendering.Ui.Menu;

// The loading screen: the title screen's backdrop and title, a panel with the stage (or, when ready,
// the pulsing prompt) and a progress bar, and a tip underneath. Drawn by the title menu while a game
// starts and over the world's first frames until the player presses a key.
// CONTRACT-STUB: W1 replaces the bodies and fills Tips.
public static class LoadingScreenPanel
{
    public const string PromptText = "Press any key to continue...";

    // 6-10 grim one-liners, original text.
    public static readonly string[] Tips = ["The dark is patient."];

    // Non-negative modulo over Tips; never throws.
    public static string PickTip(int seed) => Tips[0];

    public static void Build(MenuWidgets w, in LoadingScreenState state)
    {
    }
}
