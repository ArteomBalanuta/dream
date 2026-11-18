using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave C D: a crash never reissues an id. With WriteCheckpointOnDispose = false the server ends
// without a checkpoint, as after a kill: the immediate writes (placement, fracture, wolves, starter
// items) are in the database, the checkpoint-only state is not. The AFTER INSERT triggers keep
// worlds.next_object_id above every inserted id in the same statement, so a restart seeds the
// allocator past all of them.
public sealed class ObjectIdCrashSafetyTests(ITestOutputHelper output)
{

    [Fact]
    public void AfterACrash_EveryPersistedId_IsBelowTheStoredCounter_AndNoPersistedIdIsReissued()
    {
        using TempDatabase database = new();

        using (PersistentScriptedServer server = new(database.OpenBundle()))
        {
            server.Runtime.WriteCheckpointOnDispose = false;
            server.RunScript(CubeOnlyScript, ticks: CheckpointTicks - 10);
            Assert.True(server.BuiltCubeFractureTick > 0, "the built cube should have fractured, giving the pieces ids");
        }

        SqlitePools.Release(database.DatabasePath);
        PersistedIds crashed = ReadIds(database.DatabasePath);
        output.WriteLine($"after the crash: cubes {crashed.Cubes.Count}, pieces {crashed.Pieces.Count}, enemies {crashed.Enemies.Count}, items {crashed.Items.Count}, next_object_id {crashed.NextObjectId}");
        Assert.NotEmpty(crashed.Pieces);
        Assert.NotEmpty(crashed.Items);
        Assert.All(crashed.AllObjects, id => Assert.True(id < crashed.NextObjectId, $"id {id} is not below the stored counter {crashed.NextObjectId}"));
        Assert.All(crashed.Origins, id => Assert.True(id < crashed.NextObjectId, $"origin {id} is not below the stored counter {crashed.NextObjectId}"));
        Assert.Equal(crashed.AllObjects.Count, crashed.AllObjects.Distinct().Count());

        // Restart: the allocator starts at the counter, and a second crashed run only adds ids above it.
        using (PersistentScriptedServer restarted = new(database.OpenBundle()))
        {
            Assert.True(restarted.Runtime.ObjectIds.Next >= crashed.NextObjectId);
            Assert.True(restarted.Runtime.ObjectIds.Next > crashed.MaxId);
            restarted.Runtime.WriteCheckpointOnDispose = false;
            restarted.RunScript(CubeOnlyScript, ticks: CheckpointTicks - 10);
        }

        SqlitePools.Release(database.DatabasePath);
        PersistedIds second = ReadIds(database.DatabasePath);
        Assert.True(second.NextObjectId >= crashed.NextObjectId);
        Assert.All(second.AllObjects, id => Assert.True(id < second.NextObjectId));
        Assert.Equal(second.AllObjects.Count, second.AllObjects.Distinct().Count());
        HashSet<long> firstRun = [.. crashed.AllObjects, .. crashed.Origins];
        foreach (long id in second.AllObjects.Where(id => !firstRun.Contains(id)))
        {
            Assert.True(id >= crashed.NextObjectId, $"id {id} was issued below the counter {crashed.NextObjectId} stored by the crashed run");
        }

        // Item rows of the crashed run are still there under the same ids.
        Assert.Empty(crashed.Items.Except(second.Items));
    }
}
