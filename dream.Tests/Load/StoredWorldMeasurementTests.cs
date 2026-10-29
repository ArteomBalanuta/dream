using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Persistence;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Dream.Tests.Integration;
#if WAVE_E_GLUE
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Dream.Client.World;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Worlds;
using Dream.Simulation.World.Geometry;
using Dream.Server.Worlds;
using Dream.Simulation.Runtime;
#endif

namespace Dream.Tests.Load;

// Persistence wave E measurements (DREAM_LOAD_TEST=1, Release) on World10k (100x8x100). The numbers
// are recorded in docs/ledger/work-log/2026-10-29-persistence-wave-e-baseline.md, measured at C0
// (the generated rows) and again on the merged build (the stored rows). The gates are in the wave E
// plan, "Measurements And Gates". The long soak is StoredWorldSoakTests; the checkpoint, burst and
// generated join-tick/transfer numbers come from the wave C/D tests (PersistenceThroughputMeasurementTests,
// IncrementalCheckpointMeasurementTests, WorldTransferMeasurementTests).
[Collection(TimingSensitiveCollection.Name)]
public sealed class StoredWorldMeasurementTests(ITestOutputHelper output)
{
    private const int Size = 100;
    private const int Rounds = 5;
    private const string GlueSkip = "wave E: needs the terrain transfer, stored hosting and the export/reset commands (A, B, C), unskip at glue";

    // Generated rows: the client map build as the client does it (decode-free generated source, physics,
    // replicated cube bodies, not headless) and the server start (bundle bind, runtime and map build).
    [LoadFact]
    public void GeneratedWorld10k_ClientMapBuild_AndServerStart()
    {
        MapDefinition definition = Definition();
        ClientBuildMilliseconds(new GeneratedMapSource(definition));
        List<double> client = [];
        for (int round = 0; round < Rounds; round++)
        {
            client.Add(ClientBuildMilliseconds(new GeneratedMapSource(definition)));
        }

        using StoredWorldDb db = new(Size, stored: false);
        ServerStartMilliseconds(db);
        List<double> start = [];
        for (int round = 0; round < Rounds; round++)
        {
            start.Add(ServerStartMilliseconds(db));
        }

        output.WriteLine($"generated World10k client map build (non-headless, physics): mean {client.Average():0.0} ms, min {client.Min():0.0}, max {client.Max():0.0} over {Rounds} builds");
        output.WriteLine($"generated World10k server start (bundle bind + runtime + map build): mean {start.Average():0.0} ms, min {start.Min():0.0}, max {start.Max():0.0} over {Rounds} starts");
        Assert.True(client.Average() > 0d && start.Average() > 0d);
    }

    // Stored rows on the C0 plumbing, back to back with the generated ones in the same process:
    // chunk bytes and fragments, payload residency, server start (chunk load, payload, runtime), and the
    // client map build from the same chunks (targets: payload <= 1.1x raw, fragments <= 6, server start and
    // client build <= 1.5x generated).
    [LoadFact]
    public void StoredWorld10k_Start_Payload_AndClientBuild_AgainstGenerated()
    {
        using StoredWorldDb stored = new(Size);
        using StoredWorldDb generated = new(Size, stored: false);
        MapDefinition definition = Definition();
        IReadOnlyList<PersistedTerrainChunk> chunks = stored.BaseChunks;
        long raw = chunks.Sum(chunk => (long)chunk.Data.Length);

        (var bundle, IReadOnlyList<PersistedTerrainChunk>? loaded) = stored.OpenBundle();
        Assert.NotNull(loaded);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        TerrainTransferPayload payload = TerrainTransferPayload.Create(bundle.Descriptor, loaded);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        bundle.Dispose();
        output.WriteLine(
            $"stored World10k: {payload.Manifest.Chunks} chunks, {raw:N0} raw bytes, {payload.Manifest.Fragments} fragments, base64 ~{(raw + 2) / 3 * 4:N0} B; " +
            $"TerrainTransferPayload.Create {watch.Elapsed.TotalMilliseconds:0.00} ms, {allocated:N0} B allocated ({allocated / (double)raw:0.000}x raw)");
        Assert.Equal(raw, payload.Manifest.Bytes);
        Assert.True(payload.Manifest.Fragments <= 6, $"{payload.Manifest.Fragments} fragments");
        Assert.True(allocated <= raw * 1.1d, "payload residency above 1.1x the raw blob bytes");
        // No per-connection copy: the payload holds the loaded blobs themselves.
        Assert.All(payload.Chunks, chunk => Assert.Contains(loaded, other => ReferenceEquals(other.Data, chunk.Data)));

        ServerStartMilliseconds(generated);
        ServerStartMilliseconds(stored);
        List<double> generatedStart = [];
        List<double> storedStart = [];
        List<double> generatedClient = [];
        List<double> storedClient = [];
        ClientBuildMilliseconds(new GeneratedMapSource(definition));
        ClientBuildMilliseconds(new StoredMapSource(definition, chunks));
        for (int round = 0; round < Rounds; round++)
        {
            generatedStart.Add(ServerStartMilliseconds(generated));
            storedStart.Add(ServerStartMilliseconds(stored, withPayload: true));
            generatedClient.Add(ClientBuildMilliseconds(new GeneratedMapSource(definition)));
            storedClient.Add(ClientBuildMilliseconds(new StoredMapSource(definition, chunks)));
        }

        output.WriteLine($"server start: generated {generatedStart.Average():0.0} ms, stored {storedStart.Average():0.0} ms = {storedStart.Average() / generatedStart.Average():0.00}x (target <= 1.50x)");
        output.WriteLine($"client map build: generated {generatedClient.Average():0.0} ms, stored {storedClient.Average():0.0} ms = {storedClient.Average() / generatedClient.Average():0.00}x (target <= 1.50x)");
        Assert.True(storedStart.Average() <= generatedStart.Average() * 1.5d, "stored server start above 1.5x generated");
        Assert.True(storedClient.Average() <= generatedClient.Average() * 1.5d, "stored client map build above 1.5x generated");
    }

    // The join over loopback TCP, stored against generated in one process: terrain frame bytes
    // (<= 300,000 B), the join's total bytes against the generated join (+ <= 300,000 B), the client wall
    // time from connect to the applied world (stored <= generated + 250 ms), and the server tick mean
    // after the join (stored <= generated x 1.10).
    [LoadFact(Skip = GlueSkip)]
    public async Task StoredWorld10k_JoinTransfer_AgainstGenerated()
    {
#if WAVE_E_GLUE
        using StoredWorldDb generated = new(Size, stored: false);
        using StoredWorldDb stored = new(Size);
        (long Bytes, long TerrainBytes, double WallMs, double TickMean) generatedJoin = await MeasureJoinAsync(generated);
        (long Bytes, long TerrainBytes, double WallMs, double TickMean) storedJoin = await MeasureJoinAsync(stored);
        output.WriteLine($"generated join: {generatedJoin.Bytes:N0} B, wall {generatedJoin.WallMs:0.0} ms, tick mean {generatedJoin.TickMean:0.000} ms");
        output.WriteLine($"stored join: {storedJoin.Bytes:N0} B (terrain frames {storedJoin.TerrainBytes:N0} B), wall {storedJoin.WallMs:0.0} ms, tick mean {storedJoin.TickMean:0.000} ms");
        Assert.True(storedJoin.TerrainBytes <= 300_000, $"{storedJoin.TerrainBytes} terrain frame bytes");
        Assert.True(storedJoin.Bytes <= generatedJoin.Bytes + 300_000, "stored join transfer above generated + 300,000 B");
        Assert.True(storedJoin.WallMs <= generatedJoin.WallMs + 250d, "stored join client wall time above generated + 250 ms");
        Assert.True(storedJoin.TickMean <= generatedJoin.TickMean * 1.10d, "stored join tick mean above generated x 1.10");
#else
        await Task.CompletedTask;
#endif
    }

    // Export World10k to a new stored world (bake and write, target <= 2 s) and reset-terrain with 628
    // overrides (target <= 1 s); both are reported.
    [LoadFact(Skip = GlueSkip)]
    public void ExportWorld10k_AndResetTerrain_With628Overrides_AreFast()
    {
#if WAVE_E_GLUE
        using StoredWorldDb db = new(Size, stored: false);
        SqliteWorldCatalog catalog = new(db.Schema);
        WorldRecord source = db.Record();
        IReadOnlyList<GeometryCellKey> seeded = WorldSeeder.SeedTerrainOverrides(db.DatabasePath, source.Descriptor, 628);
        SqliteConnection.ClearAllPools();

        Stopwatch watch = Stopwatch.StartNew();
        BakedTerrain baked = WorldTerrainBaker.Bake(source, source.Seed ?? 1UL, storedChunks: null, new SqliteTerrainOverrideRepository(new Dream.Server.Persistence.Store.SqliteWorldStore(db.Schema), WorldId.Default).LoadTerrainCellOverrides());
        WorldRecord created = catalog.CreateStored(WorldId.Default, "exported", baked.Chunks, terrainOnly: false);
        double exportMs = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"export World10k ({seeded.Count} overrides baked, {baked.RemovedCells} cells removed, {baked.Bytes:N0} B): {exportMs:0.0} ms (target <= 2000)");
        Assert.True(exportMs <= 2000d);

        watch.Restart();
        TerrainResetResult reset = catalog.ResetTerrain(WorldId.Default);
        double resetMs = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"reset-terrain: cleared={reset.OverridesCleared} rubble={reset.RubbleRowsDeleted} kept={reset.CellsKeptForPlacedCubes}: {resetMs:0.0} ms (target <= 1000)");
        Assert.True(resetMs <= 1000d);
        _ = created;
#endif
    }

    private static MapDefinition Definition() =>
        new(Size, 8, Size, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);

    // The client's map build: physics, replicated cube bodies, render artifacts (not headless).
    private static double ClientBuildMilliseconds(IMapSource source)
    {
        using PhysicsIntegrator physics = new();
        Stopwatch watch = Stopwatch.StartNew();
        Map map = new(source, physics, replicatedCubeBodies: true, headless: false);
        watch.Stop();
        GC.KeepAlive(map);
        return watch.Elapsed.TotalMilliseconds;
    }

    // Bind the world, (stored) load its chunks and prebuild the payload, and build the runtime and its
    // headless map. The host is disposed again.
    private static double ServerStartMilliseconds(StoredWorldDb db, bool withPayload = false)
    {
        Stopwatch watch = Stopwatch.StartNew();
        (var bundle, IReadOnlyList<PersistedTerrainChunk>? terrain) = db.OpenBundle();
        if (withPayload && terrain != null)
        {
            _ = TerrainTransferPayload.Create(bundle.Descriptor, terrain);
        }

        using Dream.Server.DedicatedServerHost host = StoredWorldDb.CreateHost(bundle, terrain);
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

#if WAVE_E_GLUE
    private static async Task<(long Bytes, long TerrainBytes, double WallMs, double TickMean)> MeasureJoinAsync(StoredWorldDb db)
    {
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(db.CreateHost());
        Stopwatch wall = Stopwatch.StartNew();
        await using WireLoopbackClient client = await server.ConnectAndJoinAsync("measure-join");
        using ClientWorldSession world = new(server.Runtime.Map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        TerrainPersistenceLoopbackTests.ApplyAllFrames(client, world);
        wall.Stop();
        long bytes = server.ConnectionFor(client).BytesSent;
        long terrainBytes = client.Frames.Where(frame => frame.Decoded.Envelope.MessageKind == "server.terrain-chunk").Sum(frame => (long)frame.Payload.Length);
        List<double> ticks = [];
        for (int i = 0; i < 40; i++)
        {
            long started = Stopwatch.GetTimestamp();
            await server.TickAsync();
            ticks.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return (bytes, terrainBytes, wall.Elapsed.TotalMilliseconds, ticks.Average());
    }
#endif
}
