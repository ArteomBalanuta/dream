namespace Dream.Rendering.World;

// DREAM_TERRAIN_VARIATION=0|1 (default 1): whether the terrain shader breaks up the per-cell tile
// repeat (stochastic sampling) and adds world-space macro variation. 0 is one plain tile sample,
// the old look. DREAM_TERRAIN_BLEND=0|1 (default 1) is the height blend between different materials
// at block edges, DREAM_TERRAIN_RELIEF=0|1 (default 1) the bump normal from the tile height; both
// are forced off when the variation is off (the plain A/B look). Visual only, and not user
// settings. An invalid value prints one stderr line and keeps the default.
public readonly record struct TerrainSurfaceSettings(bool Enabled, bool BlendRequested = true, bool ReliefRequested = true)
{
    public const string EnvironmentVariable = "DREAM_TERRAIN_VARIATION";
    public const string BlendEnvironmentVariable = "DREAM_TERRAIN_BLEND";
    public const string ReliefEnvironmentVariable = "DREAM_TERRAIN_RELIEF";

    public static TerrainSurfaceSettings Default { get; } = new(true);

    // The value of the shader's uTerrainVariation uniform.
    public float Variation => Enabled ? 1f : 0f;

    // The blend switch (the render side also requires the tile array to be ready) and the relief switch.
    public bool Blend => Enabled && BlendRequested;

    public bool Relief => Enabled && ReliefRequested;

    public static TerrainSurfaceSettings FromEnvironment() =>
        Parse(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            Environment.GetEnvironmentVariable(BlendEnvironmentVariable),
            Environment.GetEnvironmentVariable(ReliefEnvironmentVariable),
            Console.Error.WriteLine);

    public static TerrainSurfaceSettings Parse(string? variation, string? blend, string? relief, Action<string>? warn = null) =>
        new(
            ParseFlag(variation, EnvironmentVariable, warn),
            ParseFlag(blend, BlendEnvironmentVariable, warn),
            ParseFlag(relief, ReliefEnvironmentVariable, warn));

    // The variation alone; blend and relief keep their defaults.
    public static TerrainSurfaceSettings Parse(string? value, Action<string>? warn = null) =>
        new(ParseFlag(value, EnvironmentVariable, warn));

    private static bool ParseFlag(string? value, string name, Action<string>? warn)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim())
        {
            case "1":
                return true;
            case "0":
                return false;
            default:
                warn?.Invoke($"{name}='{value}' is not 0 or 1; using the default (1).");
                return true;
        }
    }
}
