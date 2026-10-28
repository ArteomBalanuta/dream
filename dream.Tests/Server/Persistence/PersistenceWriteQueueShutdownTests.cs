using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Server.Persistence;

namespace Dream.Tests.Server.Persistence;

// Wave 8 fix loop: a disposed queue refuses work, and Dispose is bounded without letting a write
// land after it. A locked database is covered by PersistenceWriteQueueLockRetryTests: outside
// dispose its writes are retried, not dropped.
public sealed class PersistenceWriteQueueShutdownTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnqueueAfterDispose_IsRefused_AndCountedAsFailed(bool background)
    {
        PersistenceWriteQueue writes = background ? PersistenceWriteQueue.Background() : PersistenceWriteQueue.Inline();
        int ran = 0;
        Assert.True(writes.Enqueue(() => Interlocked.Increment(ref ran)));
        writes.Dispose();
        Assert.True(writes.IsClosed);

        Assert.False(writes.Enqueue(() => Interlocked.Increment(ref ran)));
        Assert.False(writes.EnqueueRead(() => Interlocked.Increment(ref ran)));
        Assert.True(writes.Flush(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, Volatile.Read(ref ran));
        Assert.Equal(1, writes.CompletedWrites);
        Assert.Equal(1, writes.FailedWrites);
        writes.Dispose();
    }

    // The join prefetch on a disposed queue completes as failed instead of never completing (or
    // restarting the drained worker).
    [Fact]
    public void PrefetchOnAClosedQueue_Fails()
    {
        using PersistenceTestDatabase database = new();
        PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using Dream.Simulation.Runtime.WorldSessionRuntime runtime = new(
            12,
            new OpenTK.Mathematics.Vector3(5f, 1.5f, 5f),
            new Dream.Simulation.Runtime.SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            persistenceWrites: writes);
        writes.Dispose();

        PersistedPlayerPrefetch prefetch = Assert.IsType<PersistedPlayerPrefetch>(runtime.BeginPersistedPlayerPrefetch("late"));
        Assert.True(prefetch.IsCompleted);
        Assert.IsType<ObjectDisposedException>(prefetch.Error);
        Assert.Equal(PersistedPlayerLoadState.Failed, runtime.GetPersistedPlayerLoadState("late"));
    }

    // The worker is stuck in a slow write past the drain deadline. Dispose drops what is still
    // queued (counted as failed), waits for the write in progress, and returns; nothing queued
    // before Dispose lands afterwards.
    [Fact]
    public void Dispose_PastTheDrainDeadline_DropsTheQueuedWrites_AndNothingLandsAfterIt()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        PersistenceWriteQueue writes = PersistenceWriteQueue.Background(TimeSpan.FromMilliseconds(200));
        using ManualResetEventSlim slowWriteEntered = new(initialState: false);
        writes.Enqueue(() =>
        {
            slowWriteEntered.Set();
            Thread.Sleep(TimeSpan.FromSeconds(1));
        });
        Assert.True(slowWriteEntered.Wait(Deadline));
        const int Queued = 50;
        for (int i = 0; i < Queued; i++)
        {
            PersistedPlayerProfile profile = PersistenceGroupCommitTests.Profile($"dropped-{i}", experience: i);
            writes.Enqueue(() => profiles.Save(profile));
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        writes.Dispose();
        elapsed.Stop();
        long rowsAtDispose = database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'dropped-%';");

        output.WriteLine($"dispose took {elapsed.Elapsed.TotalMilliseconds:0} ms; failed={writes.FailedWrites} completed={writes.CompletedWrites} rows={rowsAtDispose}");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"{elapsed.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal(Queued, writes.FailedWrites);
        Assert.Equal(0, writes.PendingCount);
        Assert.Equal(0, rowsAtDispose);
        Assert.False(writes.Enqueue(() => profiles.Save(PersistenceGroupCommitTests.Profile("dropped-late", experience: 0))));

        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'dropped-%';"));
        SqliteConnection.ClearAllPools();
    }

    // Within the deadline Dispose still drains everything (wave 7's guarantee).
    [Fact]
    public void Dispose_WithinTheDrainDeadline_RunsEveryQueuedWrite()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using ManualResetEventSlim gate = new(initialState: false);
        writes.Enqueue(() => gate.Wait(TimeSpan.FromMilliseconds(300)));
        for (int i = 0; i < 300; i++)
        {
            PersistedPlayerProfile profile = PersistenceGroupCommitTests.Profile($"drained-{i}", experience: i);
            writes.Enqueue(() => profiles.Save(profile));
        }

        writes.Dispose();

        Assert.Equal(0, writes.FailedWrites);
        Assert.Equal(300, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'drained-%';"));
        SqliteConnection.ClearAllPools();
    }
}
