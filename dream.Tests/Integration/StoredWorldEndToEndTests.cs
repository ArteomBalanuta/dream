using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.World;
using Dream.Server;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Dream.Tests.Server.Persistence.Migrations;
using static Dream.Tests.Integration.ObjectIdTestSupport;

namespace Dream.Tests.Integration;

// Persistence wave E glue: the whole operator flow in one process, every server started through the
// production ServerBootstrap path (CreatePersistenceBundle with the world selected like DREAM_WORLD,
// then CreateAuthoritativeHost) and every client joining over loopback TCP:
// fracture a generated world, --worlds export, host the export, join and match, fracture it,
// restart, join and match, --worlds reset-terrain, restart, join and match.
public sealed class StoredWorldEndToEndTests(ITestOutputHelper output)
{
    private const string ExportName = "exported";

    [Fact]
    public async Task ExportHostJoinFractureRestartResetTerrain_ThroughServerBootstrap_ClientsMatch()
    {
        using StoredWorldDb db = new(stored: false);

        // 1. The generated source world (world 1) loses terrain.
        using (SoakServer script = new(Bootstrap(db, worldSelector: null, out _)))
        {
            Assert.Equal(WorldMapSource.Generated, script.Runtime.World.MapSource);
            for (int tick = 0; tick < 1500; tick++)
            {
                script.Step(tick);
            }

            Assert.True(script.Fractures > 0);
        }

        SqlitePools.Release(db.Schema);
        long sourceOverrides = Scalar(db.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 1 AND kind = 0;");
        Assert.True(sourceOverrides > 0);

        // 2. Export it with the CLI.
        (int exit, string created, string error) = Cli(db, "export", "1", ExportName);
        Assert.True(exit == 0, error);
        string[] fields = created.TrimEnd().Split('\t');
        Assert.Equal(ExportName, fields[1]);
        Assert.StartsWith("stored:", fields[3], StringComparison.Ordinal);
        long exportedId = long.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0L, Scalar(db.DatabasePath, $"SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = {exportedId};"));
        output.WriteLine($"export: world {exportedId} {ExportName}, {sourceOverrides} removals baked out");

        // 3. Host the export (selected by name, like DREAM_WORLD) and join it.
        await JoinAndMatchAsync(db, "export-joiner", expectRubble: true);

        // 4. Fracture the stored world, restart, join.
        using (SoakServer script = new(Bootstrap(db, ExportName, out _)))
        {
            for (int tick = 0; tick < 1500; tick++)
            {
                script.Step(tick);
            }

            Assert.True(script.Fractures > 0);
        }

        SqlitePools.Release(db.Schema);
        long storedOverrides = Scalar(db.DatabasePath, $"SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = {exportedId};");
        Assert.True(storedOverrides > 0);
        await JoinAndMatchAsync(db, "fracture-joiner", expectRubble: true);

        // 5. Reset the stored world's terrain with the CLI, restart, join.
        (exit, string reset, error) = Cli(db, "reset-terrain", ExportName, "--yes");
        Assert.True(exit == 0, error);
        output.WriteLine(reset.TrimEnd());
        string[] resetFields = reset.TrimEnd().Split('\t');
        int kept = int.Parse(resetFields[5]["kept=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"cleared={storedOverrides - kept}", resetFields[3]);
        Assert.Equal((long)kept, Scalar(db.DatabasePath, $"SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = {exportedId};"));

        int sourceCellsAfterReset = await JoinAndMatchAsync(db, "reset-joiner", expectRubble: null);

        // After the reset the hosted map is the exported base again, except the kept cells.
        SqliteWorldCatalog catalog = new(db.Schema);
        WorldTerrainSource exported = catalog.ReadTerrainSource(new WorldId(exportedId));
        SqlitePools.Release(db.Schema);
        Map exportedBase = new(MapSources.ForWorld(exported.Record.Descriptor, exported.Seed, exported.StoredChunks), physics: null, headless: true);
        Assert.Equal(exportedBase.GeometryCells.Keys.Count(exportedBase.IsSourceCell) - kept, sourceCellsAfterReset);
    }

    // Starts the server for the exported world through ServerBootstrap, joins one client over TCP,
    // applies every frame, and checks the client built the same map as the server. Returns the number
    // of source cells still standing in the server's map.
    private async Task<int> JoinAndMatchAsync(StoredWorldDb db, string sessionId, bool? expectRubble)
    {
        SqlitePools.Release(db.Schema);
        DedicatedServerHost host = Bootstrap(db, ExportName, out List<string> log);
        Assert.Contains(log, line => line.Contains("stored terrain: chunks=", StringComparison.Ordinal));
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        Map map = server.Runtime.Map;
        Assert.IsType<StoredMapSource>(map.Source);
        if (expectRubble is bool rubble)
        {
            Assert.Equal(rubble, map.RubbleCells.Count > 0);
        }

        Assert.Empty(map.FindInvariantViolations());

        await using WireLoopbackClient client = await server.ConnectAndJoinAsync(sessionId);
        using ClientWorldSession world = new(map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        TerrainPersistenceLoopbackTests.ApplyAllFrames(client, world);
        Assert.Equal(WorldMapSource.Stored, world.World.MapSource);
        Assert.Equal(WorldStateChecksum.Compute(map), WorldStateChecksum.Compute(world.Map));
        Assert.Equal(
            map.GeometryCells.Keys.Where(key => key.Y > 0).ToHashSet(),
            world.Map.GeometryCells.Keys.Where(key => key.Y > 0).ToHashSet());
        Assert.Equal(map.RubbleCells.Keys.ToHashSet(), world.Map.RubbleCells.Keys.ToHashSet());
        Assert.Empty(world.Map.FindInvariantViolations());
        int sourceCells = map.GeometryCells.Keys.Count(map.IsSourceCell);
        output.WriteLine($"{sessionId}: {map.GeometryCells.Count} cells ({sourceCells} source), {map.RubbleCells.Count} rubble cells, client matches");
        await server.DisconnectAsync(client);
        return sourceCells;
    }

    // The production start path: the bundle (schema, world selection, stored terrain load and
    // validation) and the authoritative host, through TryCreateServerModeHost like RunServerMode.
    private static DedicatedServerHost Bootstrap(StoredWorldDb db, string? worldSelector, out List<string> log)
    {
        List<string> lines = [];
        DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () => ServerBootstrap.CreateAuthoritativeHost(
                ServerBootstrap.CreatePersistenceBundle(
                    db.DatabasePath,
                    MigrationTestDatabase.InitScriptPath,
                    log: lines.Add,
                    worldEnvironmentValue: worldSelector)),
            lines.Add,
            out int exitCode,
            worldSelector);
        Assert.True(host != null, $"exit {exitCode}: {string.Join(" | ", lines)}");
        log = lines;
        return host;
    }

    private static (int Exit, string Out, string Err) Cli(StoredWorldDb db, params string[] args)
    {
        SqlitePools.Release(db.Schema);
        StringWriter stdout = new();
        StringWriter stderr = new();
        int exit = WorldCli.Run(args, () => new SqliteWorldCatalog(db.Schema), stdout, stderr);
        SqlitePools.Release(db.Schema);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
