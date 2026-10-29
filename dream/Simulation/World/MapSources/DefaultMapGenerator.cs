namespace Dream.Simulation.World.MapSources;

// The startup world every host has always built. Ignores the seed.
public sealed class DefaultMapGenerator : IMapGenerator
{
    public string Key => WorldGeneratorCatalog.DefaultKey;

    public int Version => WorldGeneratorCatalog.DefaultVersion;

    public IMapSource Create(MapGenerationRequest request)
    {
        MapDefinition definition = new(
            request.Width,
            request.Height,
            request.Depth,
            WorldMapSource.Generated,
            Key,
            Version);
        return new GeneratedMapSource(definition, enrichTerrain: true);
    }
}
