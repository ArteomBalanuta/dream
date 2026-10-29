using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Xunit;

namespace Dream.Tests.Simulation.World;

public class WorldGeneratorCatalogTests
{
    [Fact]
    public void Catalog_ListsOnlyTheDefaultGenerator()
    {
        IMapGenerator generator = Assert.Single(WorldGeneratorCatalog.All);

        Assert.Equal("default", generator.Key);
        Assert.Equal(1, generator.Version);
        Assert.IsType<DefaultMapGenerator>(generator);
    }

    [Fact]
    public void TryGet_FindsTheDefaultGeneratorOnly()
    {
        Assert.True(WorldGeneratorCatalog.TryGet("default", 1, out IMapGenerator? generator));
        Assert.Same(WorldGeneratorCatalog.All[0], generator);

        Assert.False(WorldGeneratorCatalog.TryGet("default", 2, out IMapGenerator? wrongVersion));
        Assert.Null(wrongVersion);
        Assert.False(WorldGeneratorCatalog.TryGet("flat", 1, out IMapGenerator? wrongKey));
        Assert.Null(wrongKey);
        Assert.False(WorldGeneratorCatalog.TryGet("", 1, out _));
    }

    [Theory]
    [InlineData("default", 1, true)]
    [InlineData("default", 0, false)]
    [InlineData("default", 2, false)]
    [InlineData("other", 1, false)]
    [InlineData("DEFAULT", 1, false)]
    public void IsSupported_MatchesTheCatalog(string key, int version, bool expected)
    {
        Assert.Equal(expected, WorldGeneratorCatalog.IsSupported(key, version));
    }

    [Fact]
    public void DefaultGenerator_IgnoresTheSeedAndBuildsEnrichedTerrain()
    {
        IMapGenerator generator = WorldGeneratorCatalog.All[0];

        IMapSource a = generator.Create(new MapGenerationRequest(56, 8, 56, 1));
        IMapSource b = generator.Create(new MapGenerationRequest(56, 8, 56, 0xDEADBEEFUL));

        Assert.Equal(new MapDefinition(56, 8, 56, WorldMapSource.Generated, "default", 1), a.Definition);
        Assert.Equal(
            a.CreateCells().Select(record => record.Key),
            b.CreateCells().Select(record => record.Key));
        Assert.Contains(a.CreateCells(), record => record.Key.Y > 0);
    }

    [Fact]
    public void ForWorld_BuildsTheGeneratedSourceOfTheWorld()
    {
        WorldDescriptor world = WorldDescriptor.ForFloorSize(30);

        IMapSource source = MapSources.ForWorld(world, seed: 7);

        Assert.IsType<GeneratedMapSource>(source);
        Assert.Equal(MapDefinition.ForWorld(world), source.Definition);
    }

    [Fact]
    public void ForWorld_RejectsStoredAndUnknownGenerators()
    {
        WorldDescriptor stored = WorldDescriptor.ForFloorSize(30) with { MapSource = WorldMapSource.Stored };
        WorldDescriptor unknown = WorldDescriptor.ForFloorSize(30) with { GeneratorKey = "nope" };

        NotSupportedException storedError = Assert.Throws<NotSupportedException>(() => MapSources.ForWorld(stored, 1));
        Assert.Contains("stored worlds are hosted from wave E", storedError.Message);
        Assert.Throws<NotSupportedException>(() => MapSources.ForWorld(unknown, 1));
    }
}
