using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.World.Geometry;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave C tester: the plan's risk 6, quantified. A cube is placed into a cell that still holds
// rubble from an earlier fracture. Orphan rubble rows are never deleted (deferred to wave D), so
// each cycle that places and breaks a cube in the same cell adds a rubble group while the older
// one stays in the table. These tests pin what is restored, count the orphans per cycle, and
// assert the invariants that must hold regardless: ids stay unique across kinds and below the
// counter, the database stays consistent, and nothing is lost that was live.
public sealed class WaveCTesterRubbleCellEdgeTests(ITestOutputHelper output)
{
    private const int Cycles = 4;

    [Fact]
    public void PlacingAndBreakingACubeInARubbleCell_EveryCycle_KeepsIdsUnique_AndReportsTheOrphanRubble()
    {
        using TempDatabase database = new();
        GeometryCellKey cell = default;
        HashSet<long> everSeen = [];
        List<string> report = [];
        for (int cycle = 1; cycle <= Cycles; cycle++)
        {
            int rubbleAtStart;
            bool cubeAcceptedOnRubble;
            bool cubeAndRubbleCoexist = false;
            using (PersistentScriptedServer server = new(database.OpenBundle()))
            {
                cell = new GeometryCellKey((int)server.BuildCell.X, (int)server.BuildCell.Y, (int)server.BuildCell.Z);
                rubbleAtStart = server.Runtime.Map.CaptureFracturePieces().Count(piece => piece.Cell == cell);
                server.RunScript(CubeOnlyScript, ticks: CheckpointTicks - 10);
                cubeAcceptedOnRubble = server.BuiltCubeId.HasValue;
                if (cycle > 1)
                {
                    Assert.True(rubbleAtStart > 0, $"cycle {cycle}: the rubble was not restored in the cell");
                    // Was the cube placed while the rubble was there? The rubble pieces are physics
                    // bodies in the cell; record whether the map accepted the build.
                    cubeAndRubbleCoexist = cubeAcceptedOnRubble;
                }
            }

            SqlitePools.Release(database.DatabasePath);
            PersistedIds ids = ReadIds(database.DatabasePath);
            foreach (long id in ids.AllObjects.Concat(ids.Origins))
            {
                everSeen.Add(id);
            }

            Assert.Equal(ids.AllObjects.Count, ids.AllObjects.Distinct().Count());
            Assert.Empty(ids.Origins.Intersect(ids.AllObjects));
            Assert.All(ids.AllObjects.Concat(ids.Origins), id => Assert.True(id < ids.NextObjectId));
            Assert.Equal("ok", (string)ScalarText(database.DatabasePath, "PRAGMA integrity_check;")!);
            long groups = ids.Origins.Count;
            report.Add($"cycle {cycle}: rubble in cell at start {rubbleAtStart}, cube accepted {cubeAcceptedOnRubble}, db cubes {ids.Cubes.Count}, db pieces {ids.Pieces.Count}, rubble groups {groups}, next_object_id {ids.NextObjectId}");
            _ = cubeAndRubbleCoexist;
        }

        foreach (string line in report)
        {
            output.WriteLine(line);
        }

        // Quantify: the rows in the table versus what a restart puts in the cell.
        SqlitePools.Release(database.DatabasePath);
        PersistedIds final = ReadIds(database.DatabasePath);
        using PersistentScriptedServer last = new(database.OpenBundle());
        int restoredInCell = last.Runtime.Map.CaptureFracturePieces().Count(piece => piece.Cell == cell);
        output.WriteLine($"final: {final.Pieces.Count} rubble rows in {final.Origins.Count} groups; a restart puts {restoredInCell} pieces in the cell; orphan rows {final.Pieces.Count - restoredInCell}");
        Assert.True(restoredInCell > 0);
        Assert.True(restoredInCell <= final.Pieces.Count);
    }

    // A cube that is placed into a rubble cell and not broken: the cube row and the old rubble
    // rows coexist in the database. Report what a restart makes of that pair.
    [Fact]
    public void ACubeAndOldRubbleInTheSameCell_Restart_ReportsWhichSurvives_AndKeepsIdsUnique()
    {
        using TempDatabase database = new();
        GeometryCellKey cell;
        using (PersistentScriptedServer server = new(database.OpenBundle()))
        {
            cell = new GeometryCellKey((int)server.BuildCell.X, (int)server.BuildCell.Y, (int)server.BuildCell.Z);
            server.RunScript(CubeOnlyScript, ticks: CheckpointTicks - 10);
            Assert.True(server.BuiltCubeFractureTick > 0);
        }

        SqlitePools.Release(database.DatabasePath);
        PersistedIds afterFracture = ReadIds(database.DatabasePath);
        Assert.NotEmpty(afterFracture.Pieces);

        bool cubePlaced;
        bool rubbleStillThere;
        using (PersistentScriptedServer server = new(database.OpenBundle()))
        {
            rubbleStillThere = server.Runtime.Map.TryGetRubbleAtCell(cell) != null;
            server.RunScript(PlaceOnlyScript, ticks: 40);
            cubePlaced = server.Runtime.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z) != null;
            output.WriteLine($"second run: rubble in the cell at start {rubbleStillThere}; cube placed on it {cubePlaced}; rubble after placement {server.Runtime.Map.TryGetRubbleAtCell(cell) != null}");
        }

        SqlitePools.Release(database.DatabasePath);
        PersistedIds both = ReadIds(database.DatabasePath);
        output.WriteLine($"db after placing: cubes {both.Cubes.Count}, pieces {both.Pieces.Count}, origins {both.Origins.Count}");

        bool cubeRestored;
        bool rubbleRestored;
        int restoredPieces;
        using (Dream.Server.DedicatedServerHost host = Dream.Server.ServerBootstrap.CreateAuthoritativeHost(database.OpenBundle()))
        {
            Dream.Simulation.World.Map map = host.Runtime!.Map;
            cubeRestored = map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z) != null;
            rubbleRestored = map.TryGetRubbleAtCell(cell) != null;
            restoredPieces = map.CaptureFracturePieces().Count(piece => piece.Cell == cell);
            output.WriteLine($"third run (restart): cube restored {cubeRestored}, rubble restored {rubbleRestored} ({restoredPieces} pieces); cube rows {both.Cubes.Count}, rubble rows {both.Pieces.Count}");
            Assert.Equal(both.Cubes.Count > 0, cubeRestored);
        }
        SqlitePools.Release(database.DatabasePath);
        PersistedIds afterRestart = ReadIds(database.DatabasePath);
        Assert.Equal(afterRestart.AllObjects.Count, afterRestart.AllObjects.Distinct().Count());
        Assert.Empty(afterRestart.Origins.Intersect(afterRestart.AllObjects));
        Assert.Equal(both.Cubes, afterRestart.Cubes);
        Assert.Equal(both.Pieces, afterRestart.Pieces);
        Assert.All(afterRestart.AllObjects.Concat(afterRestart.Origins), id => Assert.True(id < afterRestart.NextObjectId));
        Assert.True(cubeRestored || !cubePlaced, "a placed and persisted cube was lost by the restart");
    }

    private static ScriptStep PlaceOnlyScript(int tick, PersistentScriptedServer reference)
    {
        Dictionary<string, PlayerCommandFrame> commands = new(StringComparer.Ordinal);
        foreach (string session in Sessions)
        {
            commands[session] = new PlayerCommandFrame(
                new PlayerSessionId(session),
                (ulong)tick,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
                PlayerMoveIntent.None,
                new PlayerLookIntent(0f, 0f, Vector3.UnitZ),
                PrimaryUsePressed: false);
        }

        List<(string Session, IPlayerActionPayload Payload)> actions = tick == BuildAt
            ? [(PrimarySession, new BuildUseIntent(reference.BuildCell))]
            : [];
        return new ScriptStep(commands, actions);
    }

    private static object? ScalarText(string databasePath, string sql)
    {
        using SqliteConnection connection = Open(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
