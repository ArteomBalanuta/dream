namespace Dream.Client.Scenes;

// The loading screen's side of the play scene: world readiness, and the gate that holds input back
// until the player presses a key. The window draws the overlay from these members.
// CONTRACT-STUB: W2 implements these (default: Off, nothing shown, nothing blocked).
public partial class PlayScene
{
    // Once, before the first Update; a scene never told stays Off.
    public void BeginLoadingScreen(LoadingGateMode mode)
    {
    }

    public bool LoadingOverlayVisible => false;

    public bool LoadingAwaitingKey => false;

    public bool LoadingBlocksInput => false;

    // WorldReadiness.Progress01.
    public float LoadingProgress01 => 0f;

    public string LoadingStage => string.Empty;

    // The renderer is unavailable: let the player in without the prompt.
    public void SkipLoadingScreen()
    {
    }

    // The scripted "any key" of DREAM_MENU_FLOW.
    internal void PressContinue()
    {
    }
}
