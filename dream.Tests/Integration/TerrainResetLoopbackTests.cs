using Xunit;
using Xunit.Abstractions;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Dream.Client.World;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using static Dream.Tests.Integration.ObjectIdTestSupport;

namespace Dream.Tests.Integration;

// Persistence wave E, workstream D: reset-terrain end to end. Fracture a stored world, stop the server,
// reset the terrain offline (IWorldCatalog.ResetTerrain, workstream C), restart: the map is the base
// again except the cells under a saved placed cube, and a client joining over TCP matches.
public sealed class TerrainResetLoopbackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ResetTerrain_ThenRestart_RestoresTheBase_AndAClientJoinsAndMatches()
    {
        using StoredWorldDb db = new();
        int fractures;
        using (SoakServer script = new(db.CreateHost()))
        {
            for (int tick = 0; tick < 3000; tick++)
            {
                script.Step(tick);
            }

            fractures = script.Fractures;
        }

        SqliteConnection.ClearAllPools();
        Assert.True(fractures > 0);
        long overridesBefore = Scalar(db.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides;");
        long placedBefore = Scalar(db.DatabasePath, "SELECT COUNT(*) FROM placed_cube_instances;");
        Assert.True(overridesBefore > 0);

        TerrainResetResult result = new SqliteWorldCatalog(db.Schema).ResetTerrain(WorldId.Default);
        SqliteConnection.ClearAllPools();
        output.WriteLine($"reset: cleared={result.OverridesCleared} rubble={result.RubbleRowsDeleted} kept={result.CellsKeptForPlacedCubes} (placed cubes saved: {placedBefore})");
        Assert.Equal((int)overridesBefore - result.CellsKeptForPlacedCubes, result.OverridesCleared);
        Assert.Equal((long)result.CellsKeptForPlacedCubes, Scalar(db.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides;"));

        // Restart: the base map minus the kept cells.
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(db.CreateHost());
        Map restored = server.Runtime.Map;
        using Dream.Simulation.Physics.PhysicsIntegrator physics = new();
        Map expected = new(new StoredMapSource(db.Definition, db.BaseChunks), physics, headless: true);
        foreach (GeometryCellKey cell in StoredWorldChecks.ReadOverrideCells(db.DatabasePath))
        {
            Assert.True(expected.RemoveSourceCell(cell));
        }

        Assert.Equal(expected.GeometryCells.Keys.OrderBy(key => (key.X, key.Y, key.Z)), restored.GeometryCells.Keys.OrderBy(key => (key.X, key.Y, key.Z)));
        Assert.Empty(restored.FindInvariantViolations());

        await using WireLoopbackClient client = await server.ConnectAndJoinAsync("reset-joiner");
        using ClientWorldSession world = new(restored.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        TerrainPersistenceLoopbackTests.ApplyAllFrames(client, world);
        Assert.Equal(WorldStateChecksum.Compute(restored), WorldStateChecksum.Compute(world.Map));
        Assert.Equal(restored.RubbleCells.Keys.ToHashSet(), world.Map.RubbleCells.Keys.ToHashSet());
        await server.DisconnectAsync(client);
    }
}
