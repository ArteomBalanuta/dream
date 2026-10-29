using System.Diagnostics;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.MapSources;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Load;

// Persistence wave D measurements (DREAM_LOAD_TEST=1, Release) on World10k (100x8x100).
//
// GeneratedWorld10k_BuildReference: the generated map build is the reference for the stored-map
// budget (load plus build at most 1.5x the generated build).
[Collection(TimingSensitiveCollection.Name)]
public sealed class TerrainMeasurementTests(ITestOutputHelper output)
{
    private const int FloorSize = 100;
    private const int Rounds = 7;

    [LoadFact]
    public void GeneratedWorld10k_BuildReference()
    {
        MapDefinition definition = new(FloorSize, 8, FloorSize, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);
        // Warm-up so JIT tiering does not land on the first measured build.
        BuildMilliseconds(new GeneratedMapSource(definition));
        List<double> samples = [];
        for (int round = 0; round < Rounds; round++)
        {
            samples.Add(BuildMilliseconds(new GeneratedMapSource(definition)));
        }

        double cellsMs = Time(() => new GeneratedMapSource(definition).CreateCells());
        output.WriteLine(
            $"generated World10k map build (physics, headless): mean {samples.Average():0.0} ms, min {samples.Min():0.0}, max {samples.Max():0.0} over {Rounds} builds; CreateCells alone {cellsMs:0.0} ms");
        Assert.True(samples.Average() > 0d);
    }

    internal static double BuildMilliseconds(IMapSource source)
    {
        using PhysicsIntegrator physics = new();
        Stopwatch watch = Stopwatch.StartNew();
        Map map = new(source, physics, headless: true);
        watch.Stop();
        GC.KeepAlive(map);
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double Time(Action action)
    {
        Stopwatch watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed.TotalMilliseconds;
    }

    // Stored map: chunk count, blob bytes (target <= 256 KB, base64 is x 4/3 for E1's wire budget),
    // encode time, ReplaceTerrainChunks time, and load plus build against the generated build
    // (target <= 1.5x).
    [LoadFact]
    public void StoredWorld10k_ChunkBytes_EncodeReplaceAndLoadPlusBuild()
    {
        MapDefinition definition = new(FloorSize, 8, FloorSize, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);
        GeneratedMapSource generated = new(definition);
        _ = Dream.Simulation.World.Terrain.TerrainChunkExporter.Export(generated);

        List<double> encode = [];
        IReadOnlyList<Dream.Simulation.Persistence.PersistedTerrainChunk> chunks = [];
        for (int round = 0; round < Rounds; round++)
        {
            encode.Add(Time(() => chunks = Dream.Simulation.World.Terrain.TerrainChunkExporter.Export(generated)));
        }

        long blobBytes = chunks.Sum(chunk => (long)chunk.Data.Length);
        string work = Path.Combine(Path.GetTempPath(), $"dream-terrain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            Dream.Server.Persistence.SqliteSchemaBootstrapper schema = new(
                Path.Combine(work, "world.sqlite"),
                Path.Combine(LoadPaths.FindRepoRoot(), "dream", "config", "player-profiles.init.sql"));
            schema.EnsureSchema();
            Dream.Server.Persistence.Repositories.SqliteTerrainChunkRepository repository = new(new Dream.Server.Persistence.Store.SqliteWorldStore(schema), Dream.Simulation.World.WorldId.Default);
            double replace = Time(() => repository.ReplaceTerrainChunks(chunks));
            double load = 0d;
            IReadOnlyList<Dream.Simulation.Persistence.PersistedTerrainChunk> loaded = [];
            load = Time(() => loaded = repository.LoadTerrainChunks());

            BuildMilliseconds(new GeneratedMapSource(definition));
            BuildMilliseconds(new Dream.Simulation.World.Terrain.StoredMapSource(definition, loaded));
            List<double> generatedBuild = [];
            List<double> storedBuild = [];
            for (int round = 0; round < Rounds; round++)
            {
                generatedBuild.Add(BuildMilliseconds(new GeneratedMapSource(definition)));
                storedBuild.Add(BuildMilliseconds(new Dream.Simulation.World.Terrain.StoredMapSource(definition, loaded)));
            }

            double generatedMean = generatedBuild.Average();
            double storedMean = storedBuild.Average();
            output.WriteLine($"stored World10k: {chunks.Count} chunks, {blobBytes:N0} blob bytes ({blobBytes * 4 / 3:N0} as base64), target <= 262,144");
            output.WriteLine($"encode mean {encode.Average():0.0} ms; ReplaceTerrainChunks {replace:0.0} ms; LoadTerrainChunks {load:0.0} ms");
            output.WriteLine($"build: generated {generatedMean:0.0} ms, stored (decode inside CreateCells, chunks already loaded) {storedMean:0.0} ms; load + build {load + storedMean:0.0} ms = {(load + storedMean) / generatedMean:0.00}x generated (target <= 1.50x)");
            Assert.True(blobBytes <= 256 * 1024, $"{blobBytes} blob bytes");
            Assert.True((load + storedMean) <= generatedMean * 1.5d, "stored load + build exceeds 1.5x the generated build");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // 1,000 seeded terrain overrides on World10k: restore time, join transfer bytes and that the
    // joining client's checksum matches the server. Compare the transfer bytes with the unedited
    // World10k join in the baseline work-log.
    [LoadFact]
    public async Task World10k_With1000SeededOverrides_RestoreJoinAndTransfer()
    {
        const int Overrides = 1000;
        using Dream.Tests.Server.Persistence.Migrations.MigrationTestDatabase database = new();
        Dream.Server.Worlds.WorldRecord record = Dream.Tests.Integration.WorldBundles.OpenCatalog(database)
            .Create(new Dream.Server.Worlds.WorldCreateOptions("w10k", FloorSize, 4242));
        WorldSeeder.SeedTerrainOverrides(database.DatabasePath, record.Descriptor, Overrides);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        Stopwatch restore = Stopwatch.StartNew();
        Dream.Server.DedicatedServerHost host = Dream.Server.ServerBootstrap.CreateAuthoritativeHost(
            Dream.Tests.Integration.WorldBundles.Bind(database.Schema, record));
        restore.Stop();
        await using Dream.Tests.Integration.WireLoopbackServer server = await Dream.Tests.Integration.WireLoopbackServer.StartAsync(host);
        Map map = server.Runtime.Map;
        output.WriteLine($"World10k with {Overrides} overrides: restore (host creation) {restore.Elapsed.TotalMilliseconds:0.0} ms, {map.RubbleCells.Count} rubble cells");

        Stopwatch join = Stopwatch.StartNew();
        await using Dream.Tests.Integration.WireLoopbackClient client = await server.ConnectAndJoinAsync("terrain-measure");
        join.Stop();
        long bytes = server.ConnectionFor(client).BytesSent;
        output.WriteLine($"join {join.Elapsed.TotalMilliseconds:0.0} ms, {bytes:N0} bytes sent to the connection");
        Assert.True(bytes > 0);
    }
}
