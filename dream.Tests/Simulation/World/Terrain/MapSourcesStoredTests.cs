using OpenTK.Mathematics;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Xunit;

namespace Dream.Tests.Simulation.World.Terrain;

// Persistence wave E, C0: a stored world is built from its terrain chunks through the same runtime
// plumbing as a generated world, and the two are indistinguishable.
public class MapSourcesStoredTests
{
    private const ulong Seed = 4242;

    private static WorldDescriptor Generated(int size) => WorldDescriptor.ForFloorSize(size);

    private static WorldDescriptor Stored(int size) => Generated(size) with { MapSource = WorldMapSource.Stored };

    private static IReadOnlyList<PersistedTerrainChunk> ExportOf(WorldDescriptor generated) =>
        TerrainChunkExporter.Export(MapSources.ForWorld(generated, Seed));

    [Fact]
    public void AStoredRuntime_HasTheChecksumAndCellSetOfTheGeneratedOne()
    {
        WorldDescriptor generated = Generated(56);
        IReadOnlyList<PersistedTerrainChunk> chunks = ExportOf(generated);

        using WorldSessionRuntime generatedRuntime = new(
            generated, new Vector3(5f, 1.5f, 5f), new SceneEventHub(), simulationSeed: Seed);
        using WorldSessionRuntime storedRuntime = new(
            Stored(56), new Vector3(5f, 1.5f, 5f), new SceneEventHub(), simulationSeed: Seed, storedTerrain: chunks);

        Assert.Same(chunks, storedRuntime.StoredTerrain);
        Assert.Null(generatedRuntime.StoredTerrain);
        Assert.Equal(generatedRuntime.Map.WorldChecksum, storedRuntime.Map.WorldChecksum);
        Assert.Equal(generatedRuntime.Map.GeometryCells.Count, storedRuntime.Map.GeometryCells.Count);
        Assert.True(generatedRuntime.Map.GeometryCells.Keys.ToHashSet().SetEquals(storedRuntime.Map.GeometryCells.Keys));
        Assert.Equal(generatedRuntime.Physics.MapCubeBodies.Count, storedRuntime.Physics.MapCubeBodies.Count);
        Assert.Empty(storedRuntime.Map.FindInvariantViolations());
    }

    [Fact]
    public void AGeneratedWorld_WithChunks_Throws_AndAStoredWorldWithoutChunks_Throws()
    {
        IReadOnlyList<PersistedTerrainChunk> chunks = ExportOf(Generated(20));

        Assert.Throws<ArgumentException>(() => MapSources.ForWorld(Generated(20), Seed, chunks));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => MapSources.ForWorld(Stored(20), Seed));
        Assert.Equal("stored world has no terrain chunks", error.Message);
        Assert.Throws<ArgumentException>(() => new WorldSessionRuntime(
            Generated(20), new Vector3(5f, 1.5f, 5f), new SceneEventHub(), storedTerrain: chunks));
        Assert.Throws<InvalidOperationException>(() => new WorldSessionRuntime(
            Stored(20), new Vector3(5f, 1.5f, 5f), new SceneEventHub()));
    }

    [Fact]
    public void AStoredSource_IsAStoredMapSource_WithTheWorldsDefinition()
    {
        WorldDescriptor stored = Stored(20);

        IMapSource source = MapSources.ForWorld(stored, Seed, ExportOf(Generated(20)));

        Assert.IsType<StoredMapSource>(source);
        Assert.Equal(MapDefinition.ForWorld(stored), source.Definition);
    }

    [Fact]
    public void AStoredRuntime_CannotBeResized()
    {
        using WorldSessionRuntime runtime = new(
            Stored(20), new Vector3(5f, 1.5f, 5f), new SceneEventHub(), storedTerrain: ExportOf(Generated(20)));

        Assert.Throws<NotSupportedException>(() => runtime.RebuildMapForFloorSize(30));
        Assert.Equal(20, runtime.Map.Width);
    }

    [Fact]
    public void PersistencePorts_CarryTheTerrain_IntoTheDefaultHost()
    {
        IReadOnlyList<PersistedTerrainChunk> chunks = ExportOf(Generated(20));
        RuntimePersistencePorts ports = new(null, null, null, null, null, null, null)
        {
            Descriptor = Stored(20),
            StoredTerrain = chunks
        };

        using RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault(ports);

        Assert.Same(chunks, host.Runtime.StoredTerrain);
        Assert.Equal(WorldMapSource.Stored, host.Runtime.World.MapSource);
        Assert.Null(RuntimeAuthoritativeSessionHost.CreateDefault().Runtime.StoredTerrain);
    }

    [Fact]
    public void TheStoredSourceBudget_IsPerCreateCellsCall()
    {
        WorldDescriptor generated = Generated(20);
        IReadOnlyList<PersistedTerrainChunk> chunks = ExportOf(generated);
        long total = TerrainChunkBudgetProbe.Total(MapDefinition.ForWorld(generated), chunks);

        StoredMapSource exact = new(MapDefinition.ForWorld(generated), chunks, maxSubcellOverrides: total);
        _ = exact.CreateCells();
        _ = exact.CreateCells();

        if (total > 0)
        {
            StoredMapSource tight = new(MapDefinition.ForWorld(generated), chunks, maxSubcellOverrides: total - 1);
            Assert.Throws<InvalidDataException>(() => tight.CreateCells());
        }
    }
}

internal static class TerrainChunkBudgetProbe
{
    // The decoded subcell-override entries of all chunks.
    public static long Total(MapDefinition definition, IReadOnlyList<PersistedTerrainChunk> chunks)
    {
        long budget = long.MaxValue;
        foreach (PersistedTerrainChunk chunk in chunks)
        {
            _ = TerrainChunkCodec.Decode(definition, chunk.ChunkX, chunk.ChunkZ, chunk.Data, ref budget);
        }

        return long.MaxValue - budget;
    }
}
