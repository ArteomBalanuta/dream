namespace Dream.Simulation.World.MapSources;

// What a map is made of, independent of how its cells are produced: its size and where its terrain
// comes from. The six fields are the map-defining part of a WorldDescriptor.
public sealed record MapDefinition(
    int Width,
    int Height,
    int Depth,
    WorldMapSource Source,
    string GeneratorKey,
    int GeneratorVersion)
{
    public static MapDefinition ForWorld(WorldDescriptor world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return new MapDefinition(
            world.Width,
            world.Height,
            world.Depth,
            world.MapSource,
            world.GeneratorKey,
            world.GeneratorVersion);
    }
}
