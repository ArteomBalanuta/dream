using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using Xunit.Abstractions;
using Dream.Server.Persistence;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave 13 tester, determinism through the extracted classes on the production persistent host: two
// servers on two databases holding the same stored seed run PersistentWorldSeedDeterminismIntegrationTests'
// script (walk, build, three players fighting the scripted enemy and then the runtime's wolves,
// the primary breaking its cube into rubble) for 600 ticks in lockstep. That crosses the 5 s
// periodic checkpoint (tick 320), so the combat resolver, the inventory service and the
// persistence bridge (immediate fracture writes, incremental checkpoint, final checkpoint) all run.
// Every tick's SimulationStateChecksum must match, and after both hosts are disposed the two
// databases must hold the same world, ignoring timestamps.
public sealed class Wave13PersistentLockstepDeterminismTests(ITestOutputHelper output)
{
    private const int LongTicks = 600;

    [Fact]
    public void SameStoredSeed_TwoPersistentServers_StayIdenticalFor600Ticks_AndPersistTheSameWorld()
    {
        using TempDatabase left = new();
        using TempDatabase right = new();
        ulong seed;
        using (ServerPersistenceBundle bundle = left.OpenBundle())
        {
            seed = bundle.SimulationSeed!.Value;
        }

        SqlitePools.Release(left.DatabasePath, right.DatabasePath);
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(left.DatabasePath + suffix))
            {
                File.Copy(left.DatabasePath + suffix, right.DatabasePath + suffix);
            }
        }

        int firstDivergence = -1;
        RunRecord record;
        using (PersistentScriptedServer first = new(left.OpenBundle()))
        using (PersistentScriptedServer second = new(right.OpenBundle()))
        {
            Assert.Equal(seed, first.Seed);
            Assert.Equal(seed, second.Seed);
            Assert.Equal(first.Hash(), second.Hash());
            for (int tick = 1; tick <= LongTicks; tick++)
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
            Assert.Equal(record.FractureTick, second.Record.FractureTick);
            output.WriteLine(
                $"{LongTicks} ticks: first hit {record.FirstHitTick}, enemy died {record.EnemyDeathTick}, first fracture {record.FractureTick}, " +
                $"roll ticks {record.HitTicks.Count}, rubble pieces {record.RubblePieces}, enemies alive {first.Runtime.Enemies.ActiveActors.Count}");
        }

        Assert.True(firstDivergence < 0, $"The servers diverged at tick {firstDivergence}.");
        AssertScriptExercisedEverything(record);
        Assert.True(record.HitTicks[^1] > CheckpointTicks, $"no hit after the first periodic checkpoint (last roll tick {record.HitTicks[^1]})");

        SqlitePools.Release(left.DatabasePath, right.DatabasePath);
        string leftWorld = DumpWorld(left.DatabasePath);
        string rightWorld = DumpWorld(right.DatabasePath);
        output.WriteLine(leftWorld);
        Assert.Contains("cube_fracture_pieces", leftWorld);
        Assert.Equal(leftWorld, rightWorld);
    }
}
