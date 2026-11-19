using System.Globalization;
using OpenTK.Windowing.Common;

namespace Dream.Configuration;

/// <summary>
/// Resolves the render frame cap from the desktop client config and the <c>DREAM_MAX_FPS</c> override.
/// A value of 0 means uncapped.
/// </summary>
public static class FrameRateCap
{
    public const string EnvironmentVariable = "DREAM_MAX_FPS";
    public const int Default = 144;
    public const int Uncapped = 0;
    public const int Min = 30;

    // OpenTK clamps GameWindow.UpdateFrequency to 500 Hz.
    public const int Max = 500;

    public static int Sanitize(int value)
    {
        return value == Uncapped ? Uncapped : Math.Clamp(value, Min, Max);
    }

    public static int Resolve(int configValue, string? environmentValue)
    {
        if (!string.IsNullOrWhiteSpace(environmentValue)
            && int.TryParse(environmentValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int overrideValue))
        {
            return Sanitize(overrideValue);
        }

        return Sanitize(configValue);
    }

    public static int ResolveFromEnvironment(int configValue)
    {
        return Resolve(configValue, Environment.GetEnvironmentVariable(EnvironmentVariable));
    }

    /// <summary>
    /// The sleep-pacing frequency to hand the window. With the swap interval doing the pacing the cap
    /// only backstops a driver that ignores it, so it is lifted to twice the refresh rate unless it was
    /// deliberately set below the refresh rate. Off keeps the cap as is.
    /// </summary>
    public static int ResolveUpdateFrequency(int cap, VSyncMode mode, int refreshHz)
    {
        if (mode == VSyncMode.Off || cap == Uncapped || refreshHz <= 0 || cap < refreshHz)
        {
            return cap;
        }

        return Sanitize(Math.Max(cap, 2 * refreshHz));
    }

    public static string Describe(int hz)
    {
        return hz == Uncapped ? "uncapped" : $"{hz} FPS";
    }
}
