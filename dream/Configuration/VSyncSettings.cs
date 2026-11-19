using Dream.Client.Menu;
using OpenTK.Windowing.Common;

namespace Dream.Configuration;

/// <summary>
/// Resolves the swap interval from the desktop client config and the <c>DREAM_VSYNC</c> override.
/// Adaptive syncs to the display while frames arrive on time and tears, instead of halving to 30 fps,
/// when one is late. A capture run is always Off: it is unfocused, paced by the sleep cap and must
/// never block on the swap interval.
/// </summary>
public static class VSyncSettings
{
    public const string EnvironmentVariable = "DREAM_VSYNC";
    public const VSyncMode Default = VSyncMode.Adaptive;

    private const string HudCaptureVariable = "DREAM_HUD_CAPTURE";
    private const string MenuFlowVariable = "DREAM_MENU_FLOW";

    public static VSyncMode? Parse(string? text)
    {
        return text?.Trim().ToLowerInvariant() switch
        {
            "off" or "0" => VSyncMode.Off,
            "on" or "1" => VSyncMode.On,
            "adaptive" => VSyncMode.Adaptive,
            _ => null
        };
    }

    public static bool IsCaptureRun(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return !string.IsNullOrWhiteSpace(env(HudCaptureVariable))
            || !string.IsNullOrWhiteSpace(env(MenuCapture.EnvironmentVariable))
            || !string.IsNullOrWhiteSpace(env(MenuFlowVariable));
    }

    public static VSyncMode Resolve(VSyncMode configValue, string? environmentValue, bool captureRun)
    {
        if (captureRun)
        {
            return VSyncMode.Off;
        }

        VSyncMode? overrideValue = Parse(environmentValue);
        if (overrideValue.HasValue)
        {
            return overrideValue.Value;
        }

        return Enum.IsDefined(configValue) ? configValue : Default;
    }

    public static VSyncMode ResolveFromEnvironment(VSyncMode configValue) =>
        Resolve(
            configValue,
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            IsCaptureRun(Environment.GetEnvironmentVariable));

    public static string Describe(VSyncMode mode) => mode switch
    {
        VSyncMode.Off => "off",
        VSyncMode.On => "on",
        VSyncMode.Adaptive => "adaptive",
        _ => "off"
    };
}
