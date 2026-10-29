using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave C D: ids survive. A persistent world runs the scripted session (a cube is built and
// fractured into rubble, the scripted enemy and the runtime's wolves are fought, wolves respawn,
// a periodic checkpoint runs, the inventory holds starter items), restarts, and runs again.
//
// - Across object kinds (cubes, rubble pieces, enemies, items) no id repeats, and every id is below
//   worlds.next_object_id.
// - A restart keeps every row under the same ObjectId, binds each restored cube and enemy in the
//   identity map, and seeds the allocator at the stored counter.
// - Everything created after the restart (a new cube, its rubble, respawned wolves, new items) gets
//   an id above everything that existed before, including the retired origin id of the first cube.
public sealed class ObjectIdRestartLoopbackTests(ITestOutputHelper output)
{
    private const string NeedsAbc = "wave C: needs A+B+C, unskip at glue";

    [Fact(Skip = NeedsAbc)]
    public void ObjectIds_SurviveARestart_AFracture_ARespawn_AndACheckpoint_AndAreUniqueAcrossKinds()
    {
        using TempDatabase database = new();

        PersistedIds first;
        using (PersistentScriptedServer server = new(database.OpenBundle()))
        {
            RunRecord record = server.RunScript(Script, ticks: 600);
            AssertScriptExercisedEverything(record);
            Assert.True(record.HitTicks[^1] > CheckpointTicks, "no hit after the first periodic checkpoint");
        }

        SqliteConnection.ClearAllPools();
        first = ReadIds(database.DatabasePath);
        output.WriteLine($"first run: cubes {first.Cubes.Count}, pieces {first.Pieces.Count} (origins {string.Join(",", first.Origins)}), enemies {first.Enemies.Count}, items {first.Items.Count}, next_object_id {first.NextObjectId}");
        AssertUniqueAndBelowCounter(first);
        Assert.NotEmpty(first.Pieces);
        Assert.NotEmpty(first.Items);
        Assert.NotEmpty(first.Enemies);
        Assert.Equal(first.Pieces.Count, first.Pieces.Distinct().Count());
        // The fractured cube's row is gone but its id lives on as the pieces' origin, never a piece id.
        Assert.DoesNotContain(first.Origins, origin => first.AllObjects.Contains(origin));

        // A plain restart that ticks nothing: rows, ids and the counter are unchanged, and the identity
        // map holds exactly one binding per restored cube and enemy.
        using (Dream.Server.DedicatedServerHost host = Dream.Server.ServerBootstrap.CreateAuthoritativeHost(database.OpenBundle()))
        {
            Assert.True(host.Runtime!.ObjectIds.Next >= first.NextObjectId);
            Assert.True(host.Runtime.ObjectIds.Next > first.MaxId);
            Assert.Equal(first.Cubes.Count + first.Enemies.Count, host.Runtime.ObjectIdentities.Count);
            foreach (long id in first.Cubes.Concat(first.Enemies))
            {
                Assert.True(host.Runtime.ObjectIdentities.TryGetActorId(new Dream.Simulation.World.ObjectId(id), out _), $"object {id} has no actor binding");
            }
        }

        SqliteConnection.ClearAllPools();
        PersistedIds restarted = ReadIds(database.DatabasePath);
        Assert.Equal(first.Cubes, restarted.Cubes);
        Assert.Equal(first.Pieces, restarted.Pieces);
        Assert.Equal(first.Origins, restarted.Origins);
        Assert.Equal(first.Enemies, restarted.Enemies);
        Assert.Equal(first.Items, restarted.Items);
        Assert.Equal(first.NextObjectId, restarted.NextObjectId);

        // Run again on the restarted world: new objects only get ids above everything seen so far.
        using (PersistentScriptedServer server = new(database.OpenBundle()))
        {
            server.RunScript(Script, ticks: 150);
        }

        SqliteConnection.ClearAllPools();
        PersistedIds second = ReadIds(database.DatabasePath);
        output.WriteLine($"second run: cubes {second.Cubes.Count}, pieces {second.Pieces.Count}, enemies {second.Enemies.Count}, items {second.Items.Count}, next_object_id {second.NextObjectId}");
        AssertUniqueAndBelowCounter(second);
        Assert.True(second.NextObjectId > first.NextObjectId, "the second run allocated nothing");
        HashSet<long> seenBefore = [.. first.AllObjects, .. first.Origins];
        Assert.All(second.AllObjects.Where(id => !seenBefore.Contains(id)), id => Assert.True(id >= first.NextObjectId, $"id {id} was issued below the counter {first.NextObjectId}"));
        Assert.Empty(first.Items.Except(second.Items));
        Assert.Empty(first.Pieces.Except(second.Pieces));
    }

    private static void AssertUniqueAndBelowCounter(PersistedIds ids)
    {
        IReadOnlyList<long> all = ids.AllObjects;
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(all, id => Assert.True(id >= 1 && id < ids.NextObjectId, $"id {id} is not in [1, {ids.NextObjectId})"));
        Assert.All(ids.Origins, id => Assert.True(id < ids.NextObjectId));
    }
}
