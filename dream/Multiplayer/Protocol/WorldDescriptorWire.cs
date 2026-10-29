using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

// WorldDescriptor on the wire, in server.join-accepted:
// {"id":1,"name":"default","width":56,"height":8,"depth":56,"mapSource":"generated","generatorKey":"default","generatorVersion":1}
public sealed record WorldDescriptorWire(
    long Id,
    string Name,
    int Width,
    int Height,
    int Depth,
    string MapSource,
    string GeneratorKey,
    int GeneratorVersion)
{
    public static WorldDescriptorWire From(WorldDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new WorldDescriptorWire(
            descriptor.Id.Value,
            descriptor.Name,
            descriptor.Width,
            descriptor.Height,
            descriptor.Depth,
            WorldMapSourceText.ToStorage(descriptor.MapSource),
            descriptor.GeneratorKey,
            descriptor.GeneratorVersion);
    }

    // Does not Validate: the caller decides whether it can host the world. A bad id or map source
    // throws (ArgumentOutOfRangeException / InvalidDataException).
    public WorldDescriptor ToDescriptor()
    {
        return new WorldDescriptor(
            new WorldId(Id),
            Name,
            Width,
            Height,
            Depth,
            WorldMapSourceText.Parse(MapSource),
            GeneratorKey,
            GeneratorVersion);
    }
}
