// Persistence wave D, safety net for the stored-map path: export a generated map to chunks, store
// them in SQLite, load them back, build a map from the StoredMapSource and compare it with the map
// built from the generated source.
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Server.Worlds;
using Dream.Simulation.Persistence;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Integration;

public sealed class StoredTerrainRoundTripTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(56)]
    [InlineData(100)]
    public void ExportedChunks_SurviveSqlite_AndBuildAnEqualMap(int size)
    {
        MapDefinition definition = new(size, 8, size, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);
        GeneratedMapSource generated = new(definition);
        IReadOnlyList<PersistedTerrainChunk> exported = TerrainChunkExporter.Export(generated);
        Assert.NotEmpty(exported);

        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        SqliteTerrainChunkRepository repository = new(new SqliteWorldStore(database.Schema), WorldId.Default);
        repository.ReplaceTerrainChunks(exported);
        SqliteConnection.ClearAllPools();
        IReadOnlyList<PersistedTerrainChunk> loaded = repository.LoadTerrainChunks();

        // ORDER BY chunk_z, chunk_x, with byte-identical blobs.
        Assert.Equal(exported.Count, loaded.Count);
        for (int i = 0; i < exported.Count; i++)
        {
            Assert.Equal(exported[i].ChunkX, loaded[i].ChunkX);
            Assert.Equal(exported[i].ChunkZ, loaded[i].ChunkZ);
            Assert.Equal(exported[i].FormatVersion, loaded[i].FormatVersion);
            Assert.Equal(exported[i].CellCount, loaded[i].CellCount);
            Assert.Equal(exported[i].Data, loaded[i].Data);
        }

        StoredMapSource stored = new(definition, loaded);
        using PhysicsIntegrator generatedPhysics = new();
        using PhysicsIntegrator storedPhysics = new();
        Map generatedMap = new(generated, generatedPhysics, headless: true);
        Map storedMap = new(stored, storedPhysics, headless: true);

        Assert.Equal(generatedMap.WorldChecksum, storedMap.WorldChecksum);
        Assert.Empty(storedMap.FindInvariantViolations());
        Assert.Equal(generatedMap.GeometryCells.Count, storedMap.GeometryCells.Count);
        foreach ((GeometryCellKey key, GeometryCellRecord expected) in generatedMap.GeometryCells)
        {
            Assert.True(storedMap.GeometryCells.TryGetValue(key, out GeometryCellRecord? actual), $"missing cell {key}");
            Assert.Equal(expected.Definition.MaterialId, actual!.Definition.MaterialId);
            Assert.Equal(expected.Definition.ShapeKind, actual.Definition.ShapeKind);
            Assert.Equal(expected.Definition, actual.Definition);
            Assert.Equal(expected.SubdivisionState?.OverrideCount, actual.SubdivisionState?.OverrideCount);
            Assert.Equal(expected.SubdivisionState != null, actual.SubdivisionState != null);
            Assert.Equal(expected.SubdivisionState?.Resolution, actual.SubdivisionState?.Resolution);
            Assert.Equal(expected.SubdivisionState?.AllChildrenEmpty, actual.SubdivisionState?.AllChildrenEmpty);
        }

        Assert.Equal(generatedPhysics.MapCubeBodies.Count, storedPhysics.MapCubeBodies.Count);

        // A second build from the same chunks is identical in every checksum.
        using PhysicsIntegrator again = new();
        Map storedAgain = new(new StoredMapSource(definition, loaded), again, headless: true);
        Assert.Equal(storedMap.WorldChecksum, storedAgain.WorldChecksum);
        Assert.Equal(storedPhysics.MapCubeBodies.Count, again.MapCubeBodies.Count);
        output.WriteLine($"{size}x{size}: {exported.Count} chunks, {exported.Sum(chunk => chunk.Data.Length):N0} blob bytes, {storedMap.GeometryCells.Count} cells");
    }
}
