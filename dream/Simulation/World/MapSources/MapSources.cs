using Dream.Simulation.Persistence;
using Dream.Simulation.World.Terrain;

namespace Dream.Simulation.World.MapSources;

public static class MapSources
{
    // The terrain source of a world. Generated worlds use their catalog generator; stored worlds are
    // built from their terrain chunks, which are required for Stored and forbidden for Generated.
    public static IMapSource ForWorld(
        WorldDescriptor world,
        ulong seed,
        IReadOnlyList<PersistedTerrainChunk>? storedTerrain = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        switch (world.MapSource)
        {
            case WorldMapSource.Generated:
                if (storedTerrain != null)
                {
                    throw new ArgumentException("a generated world takes no stored terrain", nameof(storedTerrain));
                }

                if (!WorldGeneratorCatalog.TryGet(world.GeneratorKey, world.GeneratorVersion, out IMapGenerator? generator))
                {
                    throw new NotSupportedException(
                        $"World generator '{world.GeneratorKey}' version {world.GeneratorVersion} is not supported.");
                }

                return generator.Create(new MapGenerationRequest(world.Width, world.Height, world.Depth, seed));
            case WorldMapSource.Stored:
                if (storedTerrain == null)
                {
                    throw new InvalidOperationException("stored world has no terrain chunks");
                }

                return new StoredMapSource(MapDefinition.ForWorld(world), storedTerrain);
            default:
                throw new NotSupportedException($"World map source {world.MapSource} is not supported.");
        }
    }
}
