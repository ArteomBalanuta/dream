using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave C tester: two lockstep servers on two copies of one database, each restarting twice. The
// scripted session replays from tick 1 after each restart (so cubes and rubble from the earlier
// phases are restored, wolves respawn and items top up). Checksums must be equal on every tick
// of every phase, and after every phase the database dumps must be equal: object_id,
// origin_object_id and worlds.next_object_id included.
public sealed class WaveCTesterLockstepTwoRestartsTests(ITestOutputHelper output)
{
    private static readonly int[] PhaseTicks = [600, 200, 200];

    [Fact]
    public void TwoServers_RestartingTwice_KeepEqualChecksumsEveryTick_AndEqualDatabasesAfterEveryPhase()
    {
        using TempDatabase left = new();
        using TempDatabase right = new();
        using (left.OpenBundle())
        {
        }

        SqlitePools.Release(left.DatabasePath, right.DatabasePath);
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(left.DatabasePath + suffix))
            {
                File.Copy(left.DatabasePath + suffix, right.DatabasePath + suffix);
            }
        }

        long previousCounter = 0;
        for (int phase = 0; phase < PhaseTicks.Length; phase++)
        {
            int divergence = RunPhase(left, right, PhaseTicks[phase], out int compared);
            Assert.True(divergence < 0, $"phase {phase + 1}: the servers diverged at tick {divergence}");
            SqlitePools.Release(left.DatabasePath, right.DatabasePath);
            string leftDump = DumpWorld(left.DatabasePath);
            string rightDump = DumpWorld(right.DatabasePath);
            Assert.Equal(leftDump, rightDump);
            PersistedIds ids = ReadIds(left.DatabasePath);
            Assert.Equal(ReadIds(right.DatabasePath).NextObjectId, ids.NextObjectId);
            Assert.True(ids.NextObjectId >= previousCounter);
            Assert.Equal(ids.AllObjects.Count, ids.AllObjects.Distinct().Count());
            Assert.Empty(ids.Origins.Intersect(ids.AllObjects));
            previousCounter = ids.NextObjectId;
            output.WriteLine($"phase {phase + 1}: {compared} ticks equal; cubes {ids.Cubes.Count} pieces {ids.Pieces.Count} origins {ids.Origins.Count} enemies {ids.Enemies.Count} items {ids.Items.Count} next_object_id {ids.NextObjectId}; dumps equal ({leftDump.Length} chars)");
        }
    }

    private static int RunPhase(TempDatabase left, TempDatabase right, int ticks, out int compared)
    {
        int firstDivergence = -1;
        compared = 0;
        // The seed is random per database, so the cube built in an earlier phase may or may not have
        // broken by the end of it. Later phases start with it standing when it did not.
        using PersistentScriptedServer first = new(left.OpenBundle(), allowOccupiedBuildCell: true);
        using PersistentScriptedServer second = new(right.OpenBundle(), allowOccupiedBuildCell: true);
        Assert.Equal(first.Seed, second.Seed);
        Assert.Equal(first.Hash(), second.Hash());
        Assert.Equal(first.Runtime.ObjectIds.Next, second.Runtime.ObjectIds.Next);
        for (int tick = 1; tick <= ticks; tick++)
        {
            ScriptStep step = Script(tick, first);
            first.Advance(tick, step);
            second.Advance(tick, step);
            compared++;
            if (firstDivergence < 0 && first.Hash() != second.Hash())
            {
                firstDivergence = tick;
            }
        }

        Assert.Equal(first.Runtime.ObjectIds.Next, second.Runtime.ObjectIds.Next);
        return firstDivergence;
    }
}
