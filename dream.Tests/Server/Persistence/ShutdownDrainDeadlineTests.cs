using System.Collections.Concurrent;
using System.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Server.Persistence;

// Wave 9 fix loop 1 (S8): shutdown on a locked database. The background checkpoint writer and the
// runtime's final checkpoint each waited up to 10 s for the immediate-writes barrier before the
// write queue's own drain deadline started, so a held lock stretched shutdown by ~20 s (plus a
// busy_timeout per checkpoint write). Every wait now runs on the queue's one drain deadline: the
// whole host dispose is bounded by that deadline plus the busy_timeout of the attempt in flight.
public sealed class ShutdownDrainDeadlineTests(ITestOutputHelper output)
{
    private const int FloorSize = 16;
    // busy_timeout (SqliteSchemaBootstrapper): the attempt in flight when the deadline passes
    // finishes its wait for the lock before the worker stops.
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(3);

    [Fact]
    public void HostDispose_WithTheDatabaseLocked_FinishesWithinTheDrainDeadline()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        TimeSpan drainDeadline = TimeSpan.FromSeconds(3);
        PersistenceWriteQueue writes = PersistenceWriteQueue.Background(drainDeadline);
        RecordingCheckpointWriter checkpoints = new(new SqlitePersistenceCheckpointWriter(database.Schema), log: null);
        WorldSessionRuntime runtime = CreateRuntime(database, checkpoints, writes);
        DedicatedServerHost host = CreateHost(runtime);
        BackgroundCheckpointWriter background = Assert.IsType<BackgroundCheckpointWriter>(host.CheckpointWrites);
        // The runtime queues its own writes while it starts; let them land before the lock.
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, writes.FailedWrites);

        Stopwatch elapsed;
        using (SqliteConnection holder = database.Schema.CreateOpenConnection())
        using (SqliteTransaction writeLock = holder.BeginTransaction(deferred: false))
        {
            for (int i = 0; i < 5; i++)
            {
                PersistedPlayerProfile profile = PersistenceGroupCommitTests.Profile($"shutdown-locked-{i}", experience: i);
                writes.Enqueue(() => profiles.Save(profile));
            }

            // A periodic checkpoint behind those writes: its barrier cannot complete while the lock
            // is held, so the background writer is waiting on it when the host is disposed.
            PersistenceCheckpoint checkpoint = Assert.IsType<PersistenceCheckpoint>(runtime.CapturePersistenceCheckpoint(full: true));
            Assert.NotNull(checkpoint.ImmediateWritesCommitted);
            background.Enqueue(checkpoint);

            elapsed = Stopwatch.StartNew();
            host.Dispose();
            elapsed.Stop();
            writeLock.Rollback();
        }

        output.WriteLine(
            $"host dispose on a locked database took {elapsed.Elapsed.TotalMilliseconds:0} ms " +
            $"(drain deadline {drainDeadline.TotalMilliseconds:0} ms); queue failed={writes.FailedWrites} " +
            $"lockRetries={writes.LockRetries}; checkpoint writes attempted={checkpoints.Attempts} " +
            $"deferred={background.DeferredAtShutdownCheckpoints}");
        Assert.True(
            elapsed.Elapsed < drainDeadline + BusyTimeout + Slack,
            $"{elapsed.Elapsed.TotalMilliseconds:0} ms");
        Assert.True(writes.IsClosed);
        Assert.Equal(0, writes.PendingCount);
        Assert.Equal(5, writes.FailedWrites);
        // The drain saw the lock, so the dropped final checkpoint gets no fallback attempt on the
        // dispose thread (it would add a busy_timeout past the deadline).
        Assert.True(writes.LockSeenDuringShutdownDrain);
        Assert.Equal(0, checkpoints.Attempts);

        // Nothing writes after dispose returned.
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'shutdown-locked-%';"));
        SqliteConnection.ClearAllPools();
    }

    // Without a lock the shutdown path still writes the final full checkpoint, and behind every
    // immediate write queued before it: it runs on the queue in order instead of on a barrier wait.
    [Fact]
    public void HostDispose_WithoutALock_WritesTheFinalCheckpointAfterTheQueuedImmediateWrites()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        ConcurrentQueue<string> order = new();
        RecordingCheckpointWriter checkpoints = new(new SqlitePersistenceCheckpointWriter(database.Schema), order);
        WorldSessionRuntime runtime = CreateRuntime(database, checkpoints, writes);
        DedicatedServerHost host = CreateHost(runtime);
        BackgroundCheckpointWriter background = Assert.IsType<BackgroundCheckpointWriter>(host.CheckpointWrites);
        // The runtime queues its own writes while it starts; count only this test's.
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        long completedBefore = writes.CompletedWrites;

        for (int i = 0; i < 3; i++)
        {
            int index = i;
            PersistedPlayerProfile profile = PersistenceGroupCommitTests.Profile($"shutdown-clean-{i}", experience: i);
            writes.Enqueue(() =>
            {
                // Slow enough that the barrier is still pending when dispose starts.
                Thread.Sleep(50);
                profiles.Save(profile);
                order.Enqueue($"write-{index}");
            });
        }

        background.Enqueue(Assert.IsType<PersistenceCheckpoint>(runtime.CapturePersistenceCheckpoint(full: true)));
        Stopwatch elapsed = Stopwatch.StartNew();
        host.Dispose();
        elapsed.Stop();

        output.WriteLine($"clean host dispose took {elapsed.Elapsed.TotalMilliseconds:0} ms; order={string.Join(",", order)}");
        Assert.Equal(["write-0", "write-1", "write-2", "checkpoint", "checkpoint"], order);
        Assert.Equal(0, writes.FailedWrites);
        Assert.Equal(completedBefore + 3, writes.CompletedWrites);
        Assert.Equal(1, background.CompletedWrites);
        Assert.Equal(0, background.DeferredAtShutdownCheckpoints);
        Assert.Equal(3, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'shutdown-clean-%';"));
        SqliteConnection.ClearAllPools();
    }

    // Wave 9 fix loop 2 (S4): the drain deadline starts at BackgroundCheckpointWriter.Dispose, and
    // the final checkpoint is queued last, so a large but healthy backlog that outlasts the
    // deadline dropped it first, although the database was never locked. Without a lock episode the
    // final checkpoint is now written after the queue stopped, still behind every write that ran.
    [Fact]
    public void HostDispose_WithASlowUnlockedBacklogPastTheDeadline_StillWritesTheFinalCheckpoint()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        TimeSpan drainDeadline = TimeSpan.FromMilliseconds(500);
        PersistenceWriteQueue writes = PersistenceWriteQueue.Background(drainDeadline);
        ConcurrentQueue<string> order = new();
        RecordingCheckpointWriter checkpoints = new(new SqlitePersistenceCheckpointWriter(database.Schema), order);
        WorldSessionRuntime runtime = CreateRuntime(database, checkpoints, writes);
        DedicatedServerHost host = CreateHost(runtime);
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, writes.FailedWrites);

        // 40 writes of 100 ms each: about 4 s of healthy work against a 0.5 s deadline.
        const int Queued = 40;
        for (int i = 0; i < Queued; i++)
        {
            int index = i;
            PersistedPlayerProfile profile = PersistenceGroupCommitTests.Profile($"shutdown-slow-{i}", experience: i);
            writes.Enqueue(() =>
            {
                Thread.Sleep(100);
                profiles.Save(profile);
                order.Enqueue($"write-{index}");
            });
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        host.Dispose();
        elapsed.Stop();

        string[] logged = [.. order];
        int ranWrites = logged.Count(entry => entry.StartsWith("write-", StringComparison.Ordinal));
        output.WriteLine(
            $"slow unlocked host dispose took {elapsed.Elapsed.TotalMilliseconds:0} ms " +
            $"(drain deadline {drainDeadline.TotalMilliseconds:0} ms); ran={ranWrites} failed={writes.FailedWrites} " +
            $"lockRetries={writes.LockRetries}; checkpoint writes attempted={checkpoints.Attempts}");
        // The backlog really outlasted the deadline: the queue dropped part of it.
        Assert.True(writes.FailedWrites > 0, $"failed={writes.FailedWrites}");
        Assert.Equal(Queued, ranWrites + writes.FailedWrites);
        Assert.False(writes.LockSeenDuringShutdownDrain);
        Assert.Equal(0, writes.PendingCount);
        // The final checkpoint was written once, after every write that ran.
        Assert.Equal(1, checkpoints.Attempts);
        Assert.Equal("checkpoint", logged[^1]);
        Assert.Equal(1, logged.Count(entry => entry == "checkpoint"));
        Assert.Equal(ranWrites, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'shutdown-slow-%';"));

        // Nothing writes after dispose returned.
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        Assert.Equal(ranWrites, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'shutdown-slow-%';"));
        SqliteConnection.ClearAllPools();
    }

    private static WorldSessionRuntime CreateRuntime(
        PersistenceTestDatabase database,
        IPersistenceCheckpointWriter checkpointWriter,
        PersistenceWriteQueue writes)
    {
        return new WorldSessionRuntime(
            FloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            checkpointWriter: checkpointWriter,
            persistenceWrites: writes);
    }

    private static DedicatedServerHost CreateHost(WorldSessionRuntime runtime)
    {
        return new DedicatedServerHost(
            new RuntimeAuthoritativeSessionHost(runtime),
            new ProtocolRouter(Array.Empty<IServerMessageHandler>(), Array.Empty<IClientMessageHandler>()));
    }

    private sealed class RecordingCheckpointWriter(IPersistenceCheckpointWriter inner, ConcurrentQueue<string>? log)
        : IPersistenceCheckpointWriter
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public void Write(PersistenceCheckpoint checkpoint)
        {
            Interlocked.Increment(ref _attempts);
            inner.Write(checkpoint);
            log?.Enqueue("checkpoint");
        }
    }
}
