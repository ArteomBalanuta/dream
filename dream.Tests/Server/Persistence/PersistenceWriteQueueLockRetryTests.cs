using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Server.Persistence;

namespace Dream.Tests.Server.Persistence;

// Wave 8 fix loop 2 (S-E): another connection (the checkpoint writer, a DB browser, a backup) holds
// SQLite's write lock past busy_timeout (5 s). The group's BEGIN IMMEDIATE fails; that used to
// count the whole group (up to 256 cube and profile upserts) as failed and drop it, so a restart
// restored stale world state. Outside dispose the unrun writes now go back to the head of the
// queue and are retried with backoff; only past the dispose deadline are they dropped.
public sealed class PersistenceWriteQueueLockRetryTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LockHold = TimeSpan.FromSeconds(7);

    [Fact]
    public void LockHeldPastBusyTimeout_EveryQueuedWriteLands_InOrder_WithNoFailures()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        ConcurrentQueue<int> landed = new();
        const int Queued = 20;

        int maxPending = 0;
        Stopwatch elapsed;
        using (SqliteConnection holder = database.Schema.CreateOpenConnection())
        using (SqliteTransaction writeLock = holder.BeginTransaction(deferred: false))
        {
            for (int i = 0; i < Queued; i++)
            {
                int index = i;
                PersistedPlayerProfile profile = PersistenceGroupCommitTests.Profile($"retried-{i}", experience: i);
                PersistedPlayerProfile ordered = PersistenceGroupCommitTests.Profile("retried-ordered", experience: i);
                writes.Enqueue(() =>
                {
                    profiles.Save(profile);
                    profiles.Save(ordered);
                    // Reached only once the write's transaction started.
                    landed.Enqueue(index);
                });
            }

            elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < LockHold)
            {
                maxPending = Math.Max(maxPending, writes.PendingCount);
                Thread.Sleep(5);
            }

            writeLock.Rollback();
        }

        Assert.True(writes.Flush(Deadline));
        elapsed.Stop();
        output.WriteLine($"{Queued} writes behind a {LockHold.TotalSeconds:0} s lock landed after {elapsed.Elapsed.TotalMilliseconds:0} ms; lockRetries={writes.LockRetries} maxPending={maxPending} failed={writes.FailedWrites}");

        Assert.Equal(0, writes.FailedWrites);
        Assert.Equal(Queued, writes.CompletedWrites);
        Assert.Equal(0, writes.PendingCount);
        Assert.True(writes.LockRetries >= 1, $"lockRetries={writes.LockRetries}");
        // While the worker backed off, every requeued write was counted as pending again.
        Assert.Equal(Queued, maxPending);
        Assert.Equal(Enumerable.Range(0, Queued), landed);
        Assert.Equal(Queued, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'retried-%' AND player_name <> 'retried-ordered';"));
        Assert.Equal(Queued - 1, profiles.TryGet("retried-ordered")!.Value.Stats.Experience);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void ReadQueuedBehindRequeuedWrites_RunsAfterThem_AndSeesThem()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        ConcurrentQueue<string> order = new();
        PersistedPlayerProfile? seen = null;

        using (SqliteConnection holder = database.Schema.CreateOpenConnection())
        using (SqliteTransaction writeLock = holder.BeginTransaction(deferred: false))
        {
            for (int i = 1; i <= 5; i++)
            {
                int experience = i;
                writes.Enqueue(() =>
                {
                    profiles.Save(PersistenceGroupCommitTests.Profile("reader-behind-lock", experience: experience));
                    order.Enqueue($"write-{experience}");
                });
            }

            writes.EnqueueRead(() =>
            {
                seen = profiles.TryGet("reader-behind-lock");
                order.Enqueue("read");
            });
            writes.Enqueue(() =>
            {
                profiles.Save(PersistenceGroupCommitTests.Profile("reader-behind-lock", experience: 6));
                order.Enqueue("write-6");
            });

            Thread.Sleep(LockHold);
            // The read never ran ahead of the writes the lock held back.
            Assert.DoesNotContain("read", order);
            writeLock.Rollback();
        }

        Assert.True(writes.Flush(Deadline));
        output.WriteLine($"order={string.Join(",", order)} lockRetries={writes.LockRetries}");

        Assert.True(writes.LockRetries >= 1, $"lockRetries={writes.LockRetries}");
        Assert.Equal(["write-1", "write-2", "write-3", "write-4", "write-5", "read", "write-6"], order);
        Assert.Equal(5, seen!.Value.Stats.Experience);
        Assert.Equal(6, profiles.TryGet("reader-behind-lock")!.Value.Stats.Experience);
        Assert.Equal(0, writes.FailedWrites);
        Assert.Equal(6, writes.CompletedWrites);
        SqliteConnection.ClearAllPools();
    }

    // Dispose with a drain deadline shorter than the lock: the worker retries while the deadline
    // runs, then the in-flight attempt fails fast and what is queued is dropped and counted, and
    // Dispose returns within deadline + one busy_timeout (plus slack). Nothing lands afterwards.
    [Fact]
    public void Dispose_PastTheDrainDeadline_OnALockedDatabase_DropsAndCountsTheWrites_AndStaysBounded()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        TimeSpan drainDeadline = TimeSpan.FromSeconds(7);
        PersistenceWriteQueue writes = PersistenceWriteQueue.Background(drainDeadline);
        const int Queued = 20;

        Stopwatch elapsed;
        using (SqliteConnection holder = database.Schema.CreateOpenConnection())
        using (SqliteTransaction writeLock = holder.BeginTransaction(deferred: false))
        {
            for (int i = 0; i < Queued; i++)
            {
                PersistedPlayerProfile profile = PersistenceGroupCommitTests.Profile($"locked-drop-{i}", experience: i);
                writes.Enqueue(() => profiles.Save(profile));
            }

            elapsed = Stopwatch.StartNew();
            writes.Dispose();
            elapsed.Stop();
            writeLock.Rollback();
        }

        output.WriteLine($"dispose took {elapsed.Elapsed.TotalMilliseconds:0} ms; failed={writes.FailedWrites} completed={writes.CompletedWrites} lockRetries={writes.LockRetries}");
        Assert.True(elapsed.Elapsed < drainDeadline + TimeSpan.FromSeconds(10), $"{elapsed.Elapsed.TotalMilliseconds:0} ms");
        Assert.True(writes.LockRetries >= 1, $"lockRetries={writes.LockRetries}");
        Assert.Equal(Queued, writes.FailedWrites);
        Assert.Equal(0, writes.CompletedWrites);
        Assert.Equal(0, writes.PendingCount);
        Assert.False(writes.Enqueue(() => profiles.Save(PersistenceGroupCommitTests.Profile("locked-drop-late", experience: 0))));

        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'locked-drop-%';"));
        SqliteConnection.ClearAllPools();
    }

    // A short drain deadline that passes during the first lock wait: no retry, the group fails
    // fast once that wait ends, so shutdown is bounded by deadline + one busy_timeout.
    [Fact]
    public void Dispose_WithAShortDeadline_OnALockedDatabase_FailsFastWithoutRetrying()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        PersistenceWriteQueue writes = PersistenceWriteQueue.Background(TimeSpan.FromMilliseconds(200));
        const int Queued = 20;

        Stopwatch elapsed;
        using (SqliteConnection holder = database.Schema.CreateOpenConnection())
        using (SqliteTransaction writeLock = holder.BeginTransaction(deferred: false))
        {
            for (int i = 0; i < Queued; i++)
            {
                PersistedPlayerProfile profile = PersistenceGroupCommitTests.Profile($"short-drop-{i}", experience: i);
                writes.Enqueue(() => profiles.Save(profile));
            }

            elapsed = Stopwatch.StartNew();
            writes.Dispose();
            elapsed.Stop();
            writeLock.Rollback();
        }

        output.WriteLine($"dispose took {elapsed.Elapsed.TotalMilliseconds:0} ms; failed={writes.FailedWrites} lockRetries={writes.LockRetries}");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"{elapsed.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal(Queued, writes.FailedWrites);
        Assert.Equal(0, writes.PendingCount);
        Assert.Equal(0, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'short-drop-%';"));
        SqliteConnection.ClearAllPools();
    }
}
