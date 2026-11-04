using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Xunit;

namespace Dream.Tests.Simulation.World;

public class WorldGeneratorCatalogTests
{
    [Fact]
    public void Catalog_ListsTheDefaultGeneratorFirstThenTheNaturalOne()
    {
        // The default generator stays first: WorldGeneratorCatalog.All[0] is read as "the default
        // one" in places, and every world row written before another generator existed names it.
        Assert.Equal(2, WorldGeneratorCatalog.All.Count);

        IMapGenerator first = WorldGeneratorCatalog.All[0];
        Assert.Equal("default", first.Key);
        Assert.Equal(1, first.Version);
        Assert.IsType<DefaultMapGenerator>(first);

        IMapGenerator second = WorldGeneratorCatalog.All[1];
        Assert.Equal("natural", second.Key);
        Assert.Equal(1, second.Version);
        Assert.IsType<NaturalMapGenerator>(second);
    }

    [Fact]
    public void Catalog_HasNoDuplicateKeyAndVersion()
    {
        // A duplicate pair would make TryGet's answer depend on the array order, and a world row
        // names exactly this pair.
        HashSet<(string Key, int Version)> seen = [];
        foreach (IMapGenerator generator in WorldGeneratorCatalog.All)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(generator.Key),
                "a registered generator has no key");
            Assert.True(generator.Version >= 1, $"generator '{generator.Key}' has version {generator.Version}");
            Assert.True(
                seen.Add((generator.Key, generator.Version)),
                $"'{generator.Key}' version {generator.Version} is registered twice");
        }
    }

    [Fact]
    public void EveryRegisteredGenerator_IgnoresTheSeed()
    {
        // The client rebuilds a generated world from its descriptor alone
        // (ClientWorldSession.RebuildMapForDescriptor passes seed 0) because the simulation seed
        // is server-only. A generator that reads the seed would hand the client different ground
        // from the server's, and the floor half of that divergence is invisible to WorldChecksum.
        // Registering one is therefore a protocol change, not a tuning change -- this test is the
        // thing that says so.
        foreach (IMapGenerator generator in WorldGeneratorCatalog.All)
        {
            IReadOnlyList<GeometryCellRecord> withoutSeed =
                generator.Create(new MapGenerationRequest(56, 8, 56, 0)).CreateCells();
            IReadOnlyList<GeometryCellRecord> withSeed =
                generator.Create(new MapGenerationRequest(56, 8, 56, 0xDEADBEEFCAFEF00DUL)).CreateCells();

            Assert.Equal(withoutSeed.Count, withSeed.Count);
            for (int i = 0; i < withoutSeed.Count; i++)
            {
                Assert.Equal(withoutSeed[i].Key, withSeed[i].Key);
                Assert.Equal(
                    withoutSeed[i].Definition.MaterialId,
                    withSeed[i].Definition.MaterialId);
            }
        }
    }

    [Fact]
    public void TryGetLatest_PicksTheRegisteredVersionOfAKey()
    {
        Assert.True(WorldGeneratorCatalog.TryGetLatest("default", out IMapGenerator? latestDefault));
        Assert.Equal(WorldGeneratorCatalog.DefaultVersion, latestDefault.Version);

        Assert.True(WorldGeneratorCatalog.TryGetLatest("natural", out IMapGenerator? latestNatural));
        Assert.Equal(WorldGeneratorCatalog.NaturalVersion, latestNatural.Version);

        Assert.False(WorldGeneratorCatalog.TryGetLatest("nope", out IMapGenerator? missing));
        Assert.Null(missing);
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
    public void ForWorld_RejectsUnknownGenerators_AndStoredWorldsWithoutChunks()
    {
        WorldDescriptor stored = WorldDescriptor.ForFloorSize(30) with { MapSource = WorldMapSource.Stored };
        WorldDescriptor unknown = WorldDescriptor.ForFloorSize(30) with { GeneratorKey = "nope" };

        InvalidOperationException storedError = Assert.Throws<InvalidOperationException>(() => MapSources.ForWorld(stored, 1));
        Assert.Contains("stored world has no terrain chunks", storedError.Message);
        Assert.Throws<NotSupportedException>(() => MapSources.ForWorld(unknown, 1));
    }
}
