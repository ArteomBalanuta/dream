using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World.MapSources;

// Terrain built by WorldGenerator.CreateStartupGeometry, unchanged: the insertion order it returns
// is the order the startup physics bodies are added in (see MapSourceGoldenTests).
//
// materials chooses the material distribution only; the geometry walk, the cell order and the
// landmark heights are the same for every policy. Null is the classic modulo-hash scatter the
// "default" generator has always used.
public sealed class GeneratedMapSource(
    MapDefinition definition,
    bool enrichTerrain = true,
    ITerrainMaterialPolicy? materials = null) : IMapSource
{
    private readonly bool _enrichTerrain = enrichTerrain;
    private readonly ITerrainMaterialPolicy? _materials = materials;

    public MapDefinition Definition { get; } = definition ?? throw new ArgumentNullException(nameof(definition));

    public IReadOnlyList<GeometryCellRecord> CreateCells()
    {
        return new WorldGenerator(_materials).CreateStartupGeometry(
            Definition.Width,
            Definition.Height,
            Definition.Depth,
            _enrichTerrain);
    }
}
