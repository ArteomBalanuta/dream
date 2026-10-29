namespace Dream.Simulation.World.MapSources;

public readonly record struct MapGenerationRequest(int Width, int Height, int Depth, ulong Seed);

// A named, versioned way to build the base terrain of a generated world.
public interface IMapGenerator
{
    string Key { get; }

    int Version { get; }

    // The generated base terrain; the exporter turns any source into chunks.
    IMapSource Create(MapGenerationRequest request);
}
