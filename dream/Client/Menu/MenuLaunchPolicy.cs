using Dream.Configuration;

namespace Dream.Client.Menu;

// Whether the game opens on the title screen. Every automated or scripted launch skips it and keeps
// the connection screen path it always had: the remote client command line, DREAM_AUTO_CONNECT,
// DREAM_HUD_CAPTURE and DREAM_SKIP_MENU=1.
public static class MenuLaunchPolicy
{
    public const string SkipMenuEnvironmentVariable = "DREAM_SKIP_MENU";
    public const string AutoConnectEnvironmentVariable = "DREAM_AUTO_CONNECT";
    public const string HudCaptureEnvironmentVariable = "DREAM_HUD_CAPTURE";

    public static bool ShouldShowMainMenu(Func<string, string?> getEnvironmentVariable, bool remoteClientRequested = false)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        if (remoteClientRequested)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(getEnvironmentVariable(HudCaptureEnvironmentVariable)))
        {
            return false;
        }

        return HudOptions.ParseFlag(getEnvironmentVariable(SkipMenuEnvironmentVariable)) != true
            && HudOptions.ParseFlag(getEnvironmentVariable(AutoConnectEnvironmentVariable)) != true;
    }
}
