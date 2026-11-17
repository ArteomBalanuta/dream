namespace Dream.Client.Scenes;

// The loading screen's side of the play scene: world readiness, and the gate that holds input back
// until the player presses a key. The window draws the overlay from these members.
public partial class PlayScene
{
    private LoadingGate _loadingGate = new(LoadingGateMode.Off);
    private readonly WorldReadiness _readiness = new();
    private bool _readinessTimeoutLogged;

    // Once, before the first Update; a scene never told stays Off.
    public void BeginLoadingScreen(LoadingGateMode mode)
    {
        _loadingGate = new LoadingGate(mode);
    }

    public bool LoadingOverlayVisible => _loadingGate.OverlayVisible;

    public bool LoadingAwaitingKey => _loadingGate.Phase == LoadingGatePhase.AwaitingKey;

    public bool LoadingBlocksInput => _loadingGate.BlocksInput;

    // WorldReadiness.Progress01.
    public float LoadingProgress01 => _readiness.Progress01;

    public string LoadingStage => _readiness.Stage;

    // The renderer is unavailable: let the player in without the prompt.
    public void SkipLoadingScreen()
    {
        _loadingGate.Skip();
    }

    // The scripted "any key" of DREAM_MENU_FLOW.
    internal void PressContinue()
    {
        _loadingGate.PressContinue();
    }

    private void LogWorldReadinessTimeout()
    {
        if (_readiness.TimedOut && !_readinessTimeoutLogged)
        {
            _readinessTimeoutLogged = true;
            Console.Error.WriteLine($"[loading] the world was not ready {WorldReadiness.TimeoutSeconds:0} s after the join; showing it anyway");
        }
    }
}
