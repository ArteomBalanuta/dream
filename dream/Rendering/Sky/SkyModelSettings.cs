using System.Globalization;

namespace Dream.Rendering.Sky;

// NightBrightness: up-facing light at night as a fraction of noon's. T1 keeps "moonlit silhouettes"; T2d lowers it.
public readonly record struct SkyModelSettings(float NightBrightness)
{
    public const float DefaultNightBrightness = 0.15f;
    public const string NightBrightnessEnvironmentVariable = "DREAM_NIGHT_BRIGHTNESS";

    public static SkyModelSettings Default { get; } = new(DefaultNightBrightness);

    public static SkyModelSettings FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(NightBrightnessEnvironmentVariable));

    public static SkyModelSettings Parse(string? value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v)
            ? new SkyModelSettings(Math.Clamp(v, 0f, 1f))
            : Default;
}
