namespace Dream.Simulation.World.MapSources;

// The noise-driven world: the same shape as the default generator, with
// NaturalTerrainMaterialPolicy choosing the surface instead of the modulo-hash scatter.
//
// A separate key, not a new version of "default": a world row stores its generator key and
// version, so the cells "default" version 1 produces must stay byte-identical forever or every
// already-persisted generated world stops reproducing. The goldens in MapSourceGoldenTests cover
// "default" and are untouched by this generator.
//
// Ignores the seed, as the default generator does. A client rebuilds a generated world from its
// descriptor and never receives the simulation seed (it is server-only), so a seed-dependent
// generator would hand the client different ground from the server's.
public sealed class NaturalMapGenerator : IMapGenerator
{
    public string Key => WorldGeneratorCatalog.NaturalKey;

    public int Version => WorldGeneratorCatalog.NaturalVersion;

    public IMapSource Create(MapGenerationRequest request)
    {
        MapDefinition definition = new(
            request.Width,
            request.Height,
            request.Depth,
            WorldMapSource.Generated,
            Key,
            Version);
        return new GeneratedMapSource(
            definition,
            enrichTerrain: true,
            materials: NaturalTerrainMaterialPolicy.Instance);
    }
}
