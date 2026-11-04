using System.Globalization;

namespace Dream.Rendering.Mist;

// The war mist's environment switches. Visual only: nothing here is sent to the server or saved.
//   DREAM_WAR_MIST=0|1                  the mist on or off (default 1)
//   DREAM_WAR_MIST_EXPLORED=none|all    whether the map starts remembered (default none; "all" is for capture)
//   DREAM_WAR_MIST_SLICES=2..8          horizontal slices in the stack (default 6)
// An invalid value writes one line to stderr and the default is used.
public readonly record struct WarMistSettings(bool Enabled, bool ExploredAtStart, int SliceCount)
{
    public const string EnabledEnvironmentVariable = "DREAM_WAR_MIST";
    public const string ExploredEnvironmentVariable = "DREAM_WAR_MIST_EXPLORED";
    public const string SlicesEnvironmentVariable = "DREAM_WAR_MIST_SLICES";
    public const int MinSlices = 2;
    public const int MaxSlices = 8;
    public const int DefaultSlices = 6;

    public static WarMistSettings Default { get; } = new(true, false, DefaultSlices);

    // Read once per process, so an invalid value is reported once however many scenes and settings read it.
    public static WarMistSettings Current { get; } = FromEnvironment();

    public static WarMistSettings FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(EnabledEnvironmentVariable),
        Environment.GetEnvironmentVariable(ExploredEnvironmentVariable),
        Environment.GetEnvironmentVariable(SlicesEnvironmentVariable),
        Console.Error.WriteLine);

    public static WarMistSettings Parse(string? enabled, string? explored, string? slices, Action<string>? warn)
    {
        bool isEnabled = Default.Enabled;
        if (!string.IsNullOrWhiteSpace(enabled))
        {
            string text = enabled.Trim();
            if (text == "0" || text == "1")
            {
                isEnabled = text == "1";
            }
            else
            {
                warn?.Invoke($"{EnabledEnvironmentVariable}='{enabled}' is not 0 or 1; using 1.");
            }
        }

        bool exploredAtStart = Default.ExploredAtStart;
        if (!string.IsNullOrWhiteSpace(explored))
        {
            string text = explored.Trim();
            if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
            {
                exploredAtStart = string.Equals(text, "all", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                warn?.Invoke($"{ExploredEnvironmentVariable}='{explored}' is not none or all; using none.");
            }
        }

        int sliceCount = DefaultSlices;
        if (!string.IsNullOrWhiteSpace(slices))
        {
            if (int.TryParse(slices.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                && parsed >= MinSlices && parsed <= MaxSlices)
            {
                sliceCount = parsed;
            }
            else
            {
                warn?.Invoke($"{SlicesEnvironmentVariable}='{slices}' is not an integer from {MinSlices} to {MaxSlices}; using {DefaultSlices}.");
            }
        }

        return new WarMistSettings(isEnabled, exploredAtStart, sliceCount);
    }
}
