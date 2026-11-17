using System.Globalization;

namespace Dream.Configuration;

/// <summary>
/// Resolves the multisample count of the default framebuffer from the desktop client config and the
/// <c>DREAM_MSAA</c> override. The count is a window-creation hint (GLFW_SAMPLES), so it cannot change while
/// the window lives. 0 means no multisampling.
/// </summary>
public static class MsaaSettings
{
    public const string EnvironmentVariable = "DREAM_MSAA";
    public const int Default = 4;
    public const int Off = 0;

    public static readonly int[] AllowedSamples = [0, 2, 4];

    /// <summary>Snaps to the nearest allowed count, rounding down on a tie; negatives become 0.</summary>
    public static int Sanitize(int value)
    {
        int best = AllowedSamples[0];
        foreach (int candidate in AllowedSamples)
        {
            // The distances are widened to long on purpose: candidate - int.MinValue overflows an int and
            // Math.Abs(int.MinValue) throws, which would take the whole startup down before the window exists.
            if (Math.Abs((long)candidate - value) < Math.Abs((long)best - value))
            {
                best = candidate;
            }
        }

        return best;
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

    public static string Describe(int samples)
    {
        return samples <= Off ? "off" : $"{samples}x";
    }
}
