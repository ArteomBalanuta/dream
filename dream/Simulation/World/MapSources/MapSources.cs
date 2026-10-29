namespace Dream.Simulation.World.MapSources;

public static class MapSources
{
    // The terrain source of a world. Generated worlds use their catalog generator; stored worlds
    // cannot be hosted yet.
    public static IMapSource ForWorld(WorldDescriptor world, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(world);
        switch (world.MapSource)
        {
            case WorldMapSource.Generated:
                if (!WorldGeneratorCatalog.TryGet(world.GeneratorKey, world.GeneratorVersion, out IMapGenerator? generator))
                {
                    throw new NotSupportedException(
                        $"World generator '{world.GeneratorKey}' version {world.GeneratorVersion} is not supported.");
                }

                return generator.Create(new MapGenerationRequest(world.Width, world.Height, world.Depth, seed));
            case WorldMapSource.Stored:
                throw new NotSupportedException("stored worlds are hosted from wave E");
            default:
                throw new NotSupportedException($"World map source {world.MapSource} is not supported.");
        }
    }
}
