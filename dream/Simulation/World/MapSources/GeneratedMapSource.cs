using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World.MapSources;

// Terrain built by WorldGenerator.CreateStartupGeometry, unchanged: the insertion order it returns
// is the order the startup physics bodies are added in (see MapSourceGoldenTests).
public sealed class GeneratedMapSource(MapDefinition definition, bool enrichTerrain = true) : IMapSource
{
    private readonly bool _enrichTerrain = enrichTerrain;

    public MapDefinition Definition { get; } = definition ?? throw new ArgumentNullException(nameof(definition));

    public IReadOnlyList<GeometryCellRecord> CreateCells()
    {
        return new WorldGenerator().CreateStartupGeometry(
            Definition.Width,
            Definition.Height,
            Definition.Depth,
            _enrichTerrain);
    }
}
