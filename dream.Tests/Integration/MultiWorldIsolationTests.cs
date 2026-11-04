using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Server.Persistence.Migrations;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Persistence wave B: several worlds live in one database and never touch each other. The wave 13
// combat/build/fracture script runs on world "alpha", then the same script on world "beta" (the same
// local ids: cube ids, actor ids and item ids restart per world). Alpha's rows are byte-for-byte what
// they were before beta ran, the default world 1 never gains a row, and each world keeps its own seed
// and clock.
public sealed class MultiWorldIsolationTests(ITestOutputHelper output)
{
    private const ulong AlphaSeed = 1111;
    private const ulong BetaSeed = 2222;

    [Fact]
    public void TwoWorlds_RunningTheSameScript_WithTheSameLocalIds_StayIsolated()
    {
        using MigrationTestDatabase database = new();
        SqliteWorldCatalog catalog = WorldBundles.OpenCatalog(database);
        WorldRecord alpha = catalog.Create(new WorldCreateOptions("alpha", Seed: AlphaSeed));
        WorldRecord beta = catalog.Create(new WorldCreateOptions("beta", Seed: BetaSeed));
        WorldId alphaId = alpha.Descriptor.Id;
        WorldId betaId = beta.Descriptor.Id;
        Assert.Equal([1L, alphaId.Value, betaId.Value], catalog.List().Select(world => world.Descriptor.Id.Value).ToArray());
        Assert.NotEqual(alphaId, betaId);

        int? alphaCubeId;
        Vector3Cell alphaBuildCell;
        using (PersistentScriptedServer server = new(WorldBundles.Bind(database.Schema, alpha)))
        {
            Assert.Equal(AlphaSeed, server.Seed);
            server.RunScript(Script);
            alphaCubeId = server.BuiltCubeId;
            alphaBuildCell = new Vector3Cell(server.BuildCell);
            Assert.NotNull(alphaCubeId);
        }

        SqliteConnection.ClearAllPools();

        // Alpha now holds its built cube (whole, or as fracture pieces) and a clock; nobody else holds anything.
        long alphaObjects = WorldBundles.Count(database, "placed_cube_instances", alphaId)
            + WorldBundles.Count(database, "cube_fracture_pieces", alphaId);
        Assert.True(alphaObjects > 0, "alpha persisted neither its built cube nor rubble");
        foreach (string table in WorldBundles.WorldTables)
        {
            Assert.Equal(0L, WorldBundles.Count(database, table, WorldId.Default));
            Assert.Equal(0L, WorldBundles.Count(database, table, betaId));
        }

        Dictionary<string, string> alphaBefore = WorldBundles.WorldTables.ToDictionary(table => table, table => WorldBundles.Dump(database, table, alphaId));
        long alphaTime = Assert.IsType<long>(database.Scalar($"SELECT world_time_ticks FROM worlds WHERE world_id = {alphaId};"));
        Assert.True(alphaTime > 0, "alpha's clock was not saved");

        // Beta runs the same script; its local ids are alpha's, but they are beta's rows.
        using (PersistentScriptedServer server = new(WorldBundles.Bind(database.Schema, beta)))
        {
            Assert.Equal(BetaSeed, server.Seed);
            server.RunScript(Script);
            Assert.Equal(alphaCubeId, server.BuiltCubeId);
            Assert.Equal(alphaBuildCell, new Vector3Cell(server.BuildCell));
        }

        SqliteConnection.ClearAllPools();

        foreach (string table in WorldBundles.WorldTables)
        {
            Assert.Equal(alphaBefore[table], WorldBundles.Dump(database, table, alphaId));
            Assert.Equal(0L, WorldBundles.Count(database, table, WorldId.Default));
        }

        Assert.True(
            WorldBundles.Count(database, "placed_cube_instances", betaId) + WorldBundles.Count(database, "cube_fracture_pieces", betaId) > 0,
            "beta persisted neither its built cube nor rubble");
        Assert.Equal(alphaTime, database.Scalar($"SELECT world_time_ticks FROM worlds WHERE world_id = {alphaId};"));
        Assert.True(Assert.IsType<long>(database.Scalar($"SELECT world_time_ticks FROM worlds WHERE world_id = {betaId};")) > 0);
        Assert.Equal(0L, database.Scalar("SELECT world_time_ticks FROM worlds WHERE world_id = 1;"));
        Assert.Equal(AlphaSeed.ToString(), database.Scalar($"SELECT seed FROM worlds WHERE world_id = {alphaId};"));
        Assert.Equal(BetaSeed.ToString(), database.Scalar($"SELECT seed FROM worlds WHERE world_id = {betaId};"));
        Assert.Null(database.Scalar("PRAGMA foreign_key_check;"));

        // Deleting beta leaves alpha untouched.
        Assert.True(catalog.Delete(betaId));
        foreach (string table in WorldBundles.WorldTables)
        {
            Assert.Equal(0L, WorldBundles.Count(database, table, betaId));
            Assert.Equal(alphaBefore[table], WorldBundles.Dump(database, table, alphaId));
        }

        // A restart of alpha restores its cube, not beta's and not a blank map.
        using (ServerPersistenceBundle reopened = WorldBundles.Open(database, alphaId))
        using (DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(reopened))
        {
            host.Runtime!.WriteCheckpointOnDispose = false;
            Assert.Equal(AlphaSeed, host.Runtime.SimulationSeed);
            // The script's primary pounds the built cube from CubeAttackFrom to the last tick, and
            // AlphaSeed is fixed, so alpha always ends as rubble: the restart has to bring back its
            // fracture pieces and no cube in that cell.
            Assert.Equal(0L, WorldBundles.Count(database, "placed_cube_instances", alphaId));
            Assert.True(
                WorldBundles.Count(database, "cube_fracture_pieces", alphaId) > 0,
                "alpha persisted neither its built cube nor its fracture pieces");
            Assert.Null(host.Runtime.Map.TryGetCubeAtCell(alphaBuildCell.X, alphaBuildCell.Y, alphaBuildCell.Z));
            Assert.Contains(host.Runtime.Map.CaptureFracturePieces(), piece => piece.Cell == new GeometryCellKey(alphaBuildCell.X, alphaBuildCell.Y, alphaBuildCell.Z));
        }

        output.WriteLine($"alpha {alphaId}: {alphaObjects} object rows, clock {alphaTime}; beta {betaId} deleted");
    }

    // A cell of a built cube, comparable by value.
    private readonly record struct Vector3Cell(int X, int Y, int Z)
    {
        public Vector3Cell(OpenTK.Mathematics.Vector3 position)
            : this((int)position.X, (int)position.Y, (int)position.Z)
        {
        }
    }
}
