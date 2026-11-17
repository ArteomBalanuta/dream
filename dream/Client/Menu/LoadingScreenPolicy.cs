using Dream.Client.Scenes;

namespace Dream.Client.Menu;

// Whether a play session shows the loading screen and waits for a key. Off when the session did not
// start from the title menu (remote-client env, DREAM_AUTO_CONNECT, DREAM_SKIP_MENU, the connection-
// screen fallback), when DREAM_HUD_CAPTURE is set, or with DREAM_LOADING_PROMPT=0; WaitForKey otherwise,
// DREAM_MENU_FLOW included (the flow script presses the key itself).
// CONTRACT-STUB: W2 replaces the body.
public static class LoadingScreenPolicy
{
    public const string PromptEnvironmentVariable = "DREAM_LOADING_PROMPT";

    public static LoadingGateMode Resolve(bool cameFromMainMenu, Func<string, string?> environment) => LoadingGateMode.Off;
}
