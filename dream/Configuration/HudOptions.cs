namespace Dream.Configuration;

/// <summary>
/// The HUD choices of one run: whether the display-only admin key (F3) and the developer controls
/// are available, whether the ImGui surface is forced, and the configured UI scale (0 is auto).
/// The environment overrides apply to this run only and are never written back to the config file.
/// Client-only: nothing here reaches the wire, the simulation or the server.
/// </summary>
public readonly record struct HudOptions(bool AdminHudAvailable, bool ForceImGuiSurface, float UiScale)
{
    public const string AdminHudEnvironmentVariable = "DREAM_ADMIN_HUD";
    public const string SurfaceEnvironmentVariable = "DREAM_HUD";
    public const string ForcedImGuiValue = "imgui";

    // "1", "true", "on" and "yes" are on; "0", "false", "off" and "no" are off; anything else is
    // not an override.
    public static bool? ParseFlag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" => false,
            _ => null
        };
    }

    // 0 (or anything not a positive finite number) is auto; other values snap to a quarter step
    // inside UiScale.Min..UiScale.Max.
    public static float SanitizeUiScale(float value)
    {
        return float.IsFinite(value) && value > 0f ? Rendering.Ui.UiScale.Resolve(0, 0, value) : 0f;
    }

    public static HudOptions Resolve(bool configAdminHud, float configUiScale, string? adminHudEnvironment, string? surfaceEnvironment)
    {
        bool admin = ParseFlag(adminHudEnvironment) ?? configAdminHud;
        bool forceImGui = string.Equals(surfaceEnvironment?.Trim(), ForcedImGuiValue, StringComparison.OrdinalIgnoreCase);
        return new HudOptions(admin, forceImGui, SanitizeUiScale(configUiScale));
    }

    public static HudOptions ResolveFromEnvironment(DesktopClientConfig? config)
    {
        return Resolve(
            config?.AdminHud ?? false,
            config?.UiScale ?? 0f,
            Environment.GetEnvironmentVariable(AdminHudEnvironmentVariable),
            Environment.GetEnvironmentVariable(SurfaceEnvironmentVariable));
    }
}
