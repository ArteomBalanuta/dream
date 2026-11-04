namespace Dream.Rendering.World;

// DREAM_TERRAIN_VARIATION=0|1 (default 1): whether the terrain shader breaks up the per-cell tile
// repeat (stochastic sampling) and adds world-space macro variation. 0 is one plain tile sample,
// the old look. Visual only, and not a user setting. An invalid value prints one stderr line and
// keeps the default.
public readonly record struct TerrainSurfaceSettings(bool Enabled)
{
    public const string EnvironmentVariable = "DREAM_TERRAIN_VARIATION";

    public static TerrainSurfaceSettings Default { get; } = new(true);

    // The value of the shader's uTerrainVariation uniform.
    public float Variation => Enabled ? 1f : 0f;

    public static TerrainSurfaceSettings FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariable), Console.Error.WriteLine);

    public static TerrainSurfaceSettings Parse(string? value, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        switch (value.Trim())
        {
            case "1":
                return new TerrainSurfaceSettings(true);
            case "0":
                return new TerrainSurfaceSettings(false);
            default:
                warn?.Invoke($"{EnvironmentVariable}='{value}' is not 0 or 1; using the default (1).");
                return Default;
        }
    }
}
