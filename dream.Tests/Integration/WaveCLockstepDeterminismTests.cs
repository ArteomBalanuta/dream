using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave C D: lockstep with ObjectIds. Two servers on two copies of one database run the scripted
// session for 600 ticks, both restart from their own database, and run the script again. ObjectIds
// never enter the checksum, so every tick's SimulationStateChecksum must match, including the ticks
// after the restart, where restored cubes and enemies get compact actor ids in object_id order.
// The allocator is deterministic, so the databases must match too: object_id, origin_object_id and
// worlds.next_object_id included.
public sealed class WaveCLockstepDeterminismTests(ITestOutputHelper output)
{
    private const int FirstRunTicks = 600;
    private const int SecondRunTicks = 150;

    [Fact]
    public void TwoServers_WithARestartInTheMiddle_KeepEqualChecksumsEveryTick_AndEqualDatabases()
    {
        using TempDatabase left = new();
        using TempDatabase right = new();
        // Opening the bundle creates the stored seed, which the copy then shares.
        using (left.OpenBundle())
        {
        }

        DatabaseImageCopy.Seed(left.DatabasePath, right.DatabasePath);

        int firstDivergence = RunPhase(left, right, FirstRunTicks, out RunRecord firstRun);
        Assert.True(firstDivergence < 0, $"The servers diverged at tick {firstDivergence} before the restart.");
        AssertScriptExercisedEverything(firstRun);

        SqlitePools.Release(left.DatabasePath, right.DatabasePath);
        PersistedIds beforeRestart = ReadIds(left.DatabasePath);
        Assert.Equal(DumpWorld(left.DatabasePath), DumpWorld(right.DatabasePath));
        output.WriteLine($"before the restart: next_object_id {beforeRestart.NextObjectId}, cubes {beforeRestart.Cubes.Count}, pieces {beforeRestart.Pieces.Count}, enemies {beforeRestart.Enemies.Count}, items {beforeRestart.Items.Count}");

        firstDivergence = RunPhase(left, right, SecondRunTicks, out _);
        Assert.True(firstDivergence < 0, $"The servers diverged at tick {firstDivergence} after the restart.");

        SqlitePools.Release(left.DatabasePath, right.DatabasePath);
        string leftWorld = DumpWorld(left.DatabasePath);
        string rightWorld = DumpWorld(right.DatabasePath);
        output.WriteLine(leftWorld);
        Assert.Contains("cube_fracture_pieces: ", leftWorld);
        Assert.Equal(leftWorld, rightWorld);
        PersistedIds afterRestart = ReadIds(left.DatabasePath);
        Assert.Equal(ReadIds(right.DatabasePath).NextObjectId, afterRestart.NextObjectId);
        Assert.True(afterRestart.NextObjectId >= beforeRestart.NextObjectId);
    }

    // Opens a server on each database, runs the script in lockstep and disposes both (the final
    // checkpoint included). Returns the first tick whose checksums differ, or -1.
    private int RunPhase(TempDatabase left, TempDatabase right, int ticks, out RunRecord record)
    {
        int firstDivergence = -1;
        using PersistentScriptedServer first = new(left.OpenBundle());
        using PersistentScriptedServer second = new(right.OpenBundle());
        Assert.Equal(first.Seed, second.Seed);
        Assert.Equal(first.Hash(), second.Hash());
        for (int tick = 1; tick <= ticks; tick++)
        {
            ScriptStep step = Script(tick, first);
            first.Advance(tick, step);
            second.Advance(tick, step);
            if (firstDivergence < 0 && first.Hash() != second.Hash())
            {
                firstDivergence = tick;
                output.WriteLine($"tick {tick}: {first.CurrentParts} vs {second.CurrentParts}");
            }
        }

        record = first.Record;
        Assert.Equal(record.HitTicks, second.Record.HitTicks);
        return firstDivergence;
    }
}
