using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Persistence wave D: two lockstep servers on two copies of one database, each restarting twice,
// while the scripted session fractures generated terrain. Checksums must be equal on every tick of
// every phase, the rubble cells must be equal, and after every phase the database dumps (terrain
// override and chunk rows included) must be equal. The scripted secondaries fracture
// terrain in each phase; from the second phase on the restored overrides remove those cells again
// before the script replays.
public sealed class TerrainLockstepRestartTests(ITestOutputHelper output)
{
    private static readonly int[] PhaseTicks = [600, 200, 200];

    [Fact]
    public void TwoServers_WithTerrainFractures_RestartingTwice_KeepEqualChecksumsEveryTick_AndEqualDatabases()
    {
        using TempDatabase left = new();
        using TempDatabase right = new();
        using (left.OpenBundle())
        {
        }

        DatabaseImageCopy.Seed(left.DatabasePath, right.DatabasePath);

        long previousOverrides = 0;
        for (int phase = 0; phase < PhaseTicks.Length; phase++)
        {
            int divergence = RunPhase(left, right, phase, PhaseTicks[phase], out int compared, out int terrainRubble);
            Assert.True(divergence < 0, $"phase {phase + 1}: the servers diverged at tick {divergence}");
            SqlitePools.Release(left.DatabasePath, right.DatabasePath);
            // DumpWorld includes the wave D terrain tables (overrides and chunks).
            string leftDump = DumpWorld(left.DatabasePath);
            Assert.Equal(leftDump, DumpWorld(right.DatabasePath));
            PersistedIds ids = ReadIds(left.DatabasePath);
            Assert.Equal(ids.AllObjects.Count, ids.AllObjects.Distinct().Count());
            Assert.Empty(ids.Origins.Intersect(ids.AllObjects));

            // The scripted terrain fractures are stored from phase 1 on, and the override ids are
            // unique across every kind.
            long overrides = Scalar(left.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 1;");
            Assert.True(overrides > 0, $"phase {phase + 1}: no terrain override was stored");
            Assert.True(overrides >= previousOverrides);
            previousOverrides = overrides;
            Assert.Equal(
                overrides,
                Scalar(left.DatabasePath, "SELECT COUNT(DISTINCT object_id) FROM terrain_cell_overrides WHERE world_id = 1;"));
            Assert.Equal(
                0L,
                Scalar(left.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides o JOIN placed_cube_instances p ON p.world_id = o.world_id AND p.object_id = o.object_id;"));
            Assert.Empty(ids.AllObjects.Intersect(OverrideIds(left.DatabasePath)));

            output.WriteLine($"phase {phase + 1}: {compared} ticks equal; rubble cells {terrainRubble}; terrain overrides {overrides}; dumps equal ({leftDump.Length} chars)");
        }
    }

    private static int RunPhase(TempDatabase left, TempDatabase right, int phase, int ticks, out int compared, out int rubbleCells)
    {
        int firstDivergence = -1;
        compared = 0;
        // Phase 1 opens a world this test created and nothing has written to, so the scripted build cell
        // is guaranteed empty and the harness still checks it. A later phase opens a world an earlier
        // phase wrote: the seed is random per database, the cube is hit on ticks 130, 158, 186 and 214,
        // and a 200-tick phase only reaches 186, so for some seeds the cube is still standing in that
        // cell when the next phase starts. That is setup state, not a divergence, so later phases allow
        // it; both servers run the same database image, so they allow it identically.
        bool restarting = phase > 0;
        using PersistentScriptedServer first = new(left.OpenBundle(), allowOccupiedBuildCell: restarting);
        using PersistentScriptedServer second = new(right.OpenBundle(), allowOccupiedBuildCell: restarting);
        Assert.Equal(first.Seed, second.Seed);
        Assert.Equal(first.Hash(), second.Hash());
        Assert.Equal(first.Runtime.Map.WorldChecksum, second.Runtime.Map.WorldChecksum);
        Assert.Equal(first.Runtime.ObjectIds.Next, second.Runtime.ObjectIds.Next);
        for (int tick = 1; tick <= ticks; tick++)
        {
            ScriptStep step = Script(tick, first);
            first.Advance(tick, step);
            second.Advance(tick, step);
            compared++;
            if (firstDivergence < 0 &&
                (first.Hash() != second.Hash() || first.Runtime.Map.WorldChecksum != second.Runtime.Map.WorldChecksum))
            {
                firstDivergence = tick;
            }
        }

        Assert.Equal(first.Runtime.ObjectIds.Next, second.Runtime.ObjectIds.Next);
        Assert.Equal(
            first.Runtime.Map.RubbleCells.Keys.OrderBy(key => (key.X, key.Y, key.Z)).ToArray(),
            second.Runtime.Map.RubbleCells.Keys.OrderBy(key => (key.X, key.Y, key.Z)).ToArray());
        rubbleCells = first.Runtime.Map.RubbleCells.Count;
        return firstDivergence;
    }

    private static long[] OverrideIds(string databasePath)
    {
        using SqliteConnection connection = Open(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT object_id FROM terrain_cell_overrides WHERE world_id = 1;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<long> ids = [];
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return [.. ids];
    }
}
