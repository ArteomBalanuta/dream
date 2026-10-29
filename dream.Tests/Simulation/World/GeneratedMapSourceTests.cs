using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Xunit;

namespace Dream.Tests.Simulation.World;

public class GeneratedMapSourceTests
{
    private static GeneratedMapSource CreateSource(int size = 56, bool enrich = true)
    {
        return new GeneratedMapSource(
            new MapDefinition(size, 8, size, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion),
            enrich);
    }

    [Theory]
    [InlineData(56, true)]
    [InlineData(19, true)]
    [InlineData(25, false)]
    public void CreateCells_ReturnsFreshRecordsInTheSameOrderOnEveryCall(int size, bool enrich)
    {
        GeneratedMapSource source = CreateSource(size, enrich);

        IReadOnlyList<GeometryCellRecord> first = source.CreateCells();
        IReadOnlyList<GeometryCellRecord> second = source.CreateCells();

        Assert.NotEmpty(first);
        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.NotSame(first[i], second[i]);
            Assert.Equal(first[i].Key, second[i].Key);
            Assert.Equal(first[i].Definition, second[i].Definition);
            Assert.Equal(first[i].SubdivisionState != null, second[i].SubdivisionState != null);
        }
    }

    [Fact]
    public void CreateCells_MutatingOneCallsRecordsDoesNotAffectTheNext()
    {
        GeneratedMapSource source = CreateSource();
        GeometryCellRecord sculpture = source.CreateCells().First(record => record.SubdivisionState != null);

        sculpture.SubdivisionState!.SetChildSolid(new GeometrySubcellKey(0, 0, 0), true);
        sculpture.SetFracturePieces([]);

        GeometryCellRecord fresh = source.CreateCells().First(record => record.Key == sculpture.Key);
        Assert.NotSame(sculpture, fresh);
        Assert.NotSame(sculpture.SubdivisionState, fresh.SubdivisionState);
        Assert.Null(fresh.FracturePieces);
    }

    [Fact]
    public void CreateCells_MatchesTheGeneratorAndStaysInsideTheDefinitionBounds()
    {
        GeneratedMapSource source = CreateSource();
        IReadOnlyList<GeometryCellRecord> cells = source.CreateCells();
        IReadOnlyList<GeometryCellRecord> direct = new WorldGenerator().CreateStartupGeometry(56, 8, 56, enrichTerrain: true);

        Assert.Equal(direct.Select(record => record.Key), cells.Select(record => record.Key));
        Assert.All(cells, record =>
        {
            Assert.Null(record.FracturePieces);
            Assert.InRange(record.Key.X, 0, 55);
            Assert.InRange(record.Key.Y, 0, 7);
            Assert.InRange(record.Key.Z, 0, 55);
        });
        Assert.Equal(cells.Count, cells.Select(record => record.Key).Distinct().Count());
    }

    [Fact]
    public void CreateCells_FlatSourceHasOnlyFloorCells()
    {
        IReadOnlyList<GeometryCellRecord> cells = CreateSource(25, enrich: false).CreateCells();

        Assert.Equal(25 * 25, cells.Count);
        Assert.All(cells, record => Assert.Equal(0, record.Key.Y));
    }

    [Fact]
    public void MapDefinition_ForWorldCopiesTheMapFields()
    {
        WorldDescriptor world = WorldDescriptor.ForFloorSize(40);

        MapDefinition definition = MapDefinition.ForWorld(world);

        Assert.Equal(
            new MapDefinition(40, WorldDescriptor.DefaultHeight, 40, WorldMapSource.Generated, "default", 1),
            definition);
    }
}
