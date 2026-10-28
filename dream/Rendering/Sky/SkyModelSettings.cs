using System.Globalization;

namespace Dream.Rendering.Sky;

// NightBrightness: up-facing light at midnight as a fraction of noon's. The default 0.07 is a dark blue
// night (terrain about 7% of noon) in which torches and lamps carry the light; T1 used 0.15.
public readonly record struct SkyModelSettings(float NightBrightness)
{
    public const float DefaultNightBrightness = 0.07f;
    public const string NightBrightnessEnvironmentVariable = "DREAM_NIGHT_BRIGHTNESS";

    public static SkyModelSettings Default { get; } = new(DefaultNightBrightness);

    public static SkyModelSettings FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(NightBrightnessEnvironmentVariable));

    public static SkyModelSettings Parse(string? value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v)
            ? new SkyModelSettings(Math.Clamp(v, 0f, 1f))
            : Default;
}
