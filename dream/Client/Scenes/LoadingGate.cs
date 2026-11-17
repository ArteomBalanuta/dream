namespace Dream.Client.Scenes;

public enum LoadingGateMode : byte
{
    Off,
    WaitForKey
}

public enum LoadingGatePhase : byte
{
    Open,
    Loading,
    AwaitingKey,
    Releasing
}

// Holds gameplay input back from the start of a play session until the world is ready and the player
// pressed (and let go of) a key or button, so the press that dismisses the loading screen never acts.
// CONTRACT-STUB: W2 replaces the bodies.
public sealed class LoadingGate
{
    // Off => Open, WaitForKey => Loading.
    public LoadingGate(LoadingGateMode mode)
    {
    }

    public LoadingGatePhase Phase { get; private set; } = LoadingGatePhase.Open;

    // Loading or AwaitingKey.
    public bool OverlayVisible => Phase is LoadingGatePhase.Loading or LoadingGatePhase.AwaitingKey;

    public bool BlocksInput => Phase != LoadingGatePhase.Open;

    // Once per PlayScene.Update, after the network drain. Loading -> AwaitingKey when worldReady.
    // AwaitingKey arms after one frame with no input down, then any input down -> Releasing.
    // Releasing -> Open on the first frame with nothing down.
    public void Update(bool worldReady, bool anyInputDown)
    {
    }

    // A scripted press: AwaitingKey -> Releasing (unarmed is fine); ignored otherwise.
    public void PressContinue()
    {
    }

    // The renderer is unavailable: Loading/AwaitingKey -> Releasing.
    public void Skip()
    {
    }
}
