namespace Dream.Simulation.World;

// The public identity of a world: what a client needs to build its map. The seed and the world
// time are server-only and live on the server's WorldRecord, never here.
public sealed record WorldDescriptor(
    WorldId Id,
    string Name,
    int Width,
    int Height,
    int Depth,
    WorldMapSource MapSource,
    string GeneratorKey,
    int GeneratorVersion)
{
    public const int MinHorizontalSize = 5;
    public const int MaxHorizontalSize = 100;
    public const int DefaultHeight = 8;
    public const int MaxNameLength = 64;
    public const string DefaultName = "default";

    // World 1, "default", 56x8x56: exactly the map every host built before worlds became rows.
    public static WorldDescriptor Default { get; } = ForFloorSize(WorldDefaults.StartupFloorSize);

    // A generated square world of the default generator. Does not validate; call Validate.
    public static WorldDescriptor ForFloorSize(int floorSize, WorldId? id = null, string name = DefaultName)
    {
        return new WorldDescriptor(
            id ?? WorldId.Default,
            name,
            floorSize,
            DefaultHeight,
            floorSize,
            WorldMapSource.Generated,
            WorldGeneratorCatalog.DefaultKey,
            WorldGeneratorCatalog.DefaultVersion);
    }

    // Throws ArgumentException unless this build can host the world: a trimmed name of
    // 1..MaxNameLength characters, a square floor in [MinHorizontalSize, MaxHorizontalSize],
    // height DefaultHeight, and a generated map with a supported generator or a stored map with provenance.
    public void Validate()
    {
        string? trimmed = Name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"World name must be 1-{MaxNameLength} characters after trimming.", nameof(Name));
        }

        if (Width != Depth)
        {
            throw new ArgumentException($"World must be square; got {Width}x{Depth}.", nameof(Width));
        }

        if (Width < MinHorizontalSize || Width > MaxHorizontalSize)
        {
            throw new ArgumentException(
                $"World size {Width} is outside {MinHorizontalSize}-{MaxHorizontalSize}.", nameof(Width));
        }

        if (Height != DefaultHeight)
        {
            throw new ArgumentException($"World height must be {DefaultHeight}; got {Height}.", nameof(Height));
        }

        switch (MapSource)
        {
            case WorldMapSource.Generated:
                if (!WorldGeneratorCatalog.IsSupported(GeneratorKey, GeneratorVersion))
                {
                    throw new ArgumentException(
                        $"World generator '{GeneratorKey}' version {GeneratorVersion} is not supported.", nameof(GeneratorKey));
                }

                break;
            case WorldMapSource.Stored:
                // Provenance only: where the terrain came from. It is never checked against the catalog.
                if (string.IsNullOrWhiteSpace(GeneratorKey) || GeneratorVersion < 1)
                {
                    throw new ArgumentException(
                        "A stored world needs a generator key and a version >= 1 as provenance.", nameof(GeneratorKey));
                }

                break;
            default:
                throw new ArgumentException($"World map source {MapSource} is not supported.", nameof(MapSource));
        }
    }
}
