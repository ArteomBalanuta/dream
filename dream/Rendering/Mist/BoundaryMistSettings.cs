namespace Dream.Rendering.Mist;

// DREAM_BOUNDARY_MIST=0|1 (default 1): whether the smoke wall hides everything past the map edge.
// Visual only, and not a user setting. An invalid value prints one stderr line and keeps the default.
public readonly record struct BoundaryMistSettings(bool Enabled)
{
    public const string EnvironmentVariable = "DREAM_BOUNDARY_MIST";

    public static BoundaryMistSettings Default { get; } = new(true);

    public static BoundaryMistSettings FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariable), Console.Error.WriteLine);

    public static BoundaryMistSettings Parse(string? value, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        switch (value.Trim())
        {
            case "1":
                return new BoundaryMistSettings(true);
            case "0":
                return new BoundaryMistSettings(false);
            default:
                warn?.Invoke($"{EnvironmentVariable}='{value}' is not 0 or 1; using the default (1).");
                return Default;
        }
    }
}
