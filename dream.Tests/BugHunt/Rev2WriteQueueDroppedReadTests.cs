using Dream.Simulation.Persistence;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-REV2-1: the batch-unavailable path (added for BH-SIM2-2) dequeued everything the queue held and
// counted only the WRITES. A queued READ was dropped silently, and a read's action is the only thing
// that ever completes its caller: the join-time profile prefetch's Load, a checkpoint barrier's
// TrySetResult, the character list's and the inventory settle's trackers. EnqueueRead had returned
// true, so every one of those callers waited for a read that would never run - the join hang class.
//
// Now a factory failure is retried a bounded number of times (PersistenceWriteQueue.BatchOpenAttempts,
// with 100 ms then 200 ms between them, so at most ~300 ms), and if it still cannot open, the reads it
// drops are RUN, outside any batch, so their callers learn.
//
// WriteBatchFactory is process-wide state, so this class runs alone (see TimingSensitiveCollection).
[Collection(TimingSensitiveCollection.Name)]
public class Rev2WriteQueueDroppedReadTests
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WaiterTimeout = TimeSpan.FromSeconds(10);

    // The defect: the read's waiter never completed, because nothing ran its action.
    [Fact]
    public async Task AQueuedRead_IsStillCompleted_WhenNoWriteBatchCanBeOpened()
    {
        Func<IPersistenceWriteBatch> original = PersistenceWriteQueue.WriteBatchFactory;
        try
        {
            PersistenceWriteQueue.WriteBatchFactory =
                static () => throw new InvalidOperationException("no persistence backend");

            using PersistenceWriteQueue queue = PersistenceWriteQueue.Background();
            TaskCompletionSource prefetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(queue.Enqueue(static () => { }));
            Assert.True(queue.EnqueueRead(() => prefetch.TrySetResult()));

            // Bounded: a waiter that is never signalled is exactly the hang this covers.
            Task completed = await Task.WhenAny(prefetch.Task, Task.Delay(WaiterTimeout));
            Assert.True(ReferenceEquals(completed, prefetch.Task), "the dropped read never completed its waiter");

            Assert.True(queue.Flush(FlushTimeout));
            Assert.Equal(1L, queue.FailedWrites);
            Assert.Equal(0L, queue.CompletedWrites);
            Assert.Equal(0, queue.PendingCount);
        }
        finally
        {
            PersistenceWriteQueue.WriteBatchFactory = original;
        }
    }

    // A read that throws must not keep the reads behind it from running: each is guarded on its own.
    [Fact]
    public async Task AThrowingDroppedRead_DoesNotStopTheReadsBehindIt()
    {
        Func<IPersistenceWriteBatch> original = PersistenceWriteQueue.WriteBatchFactory;
        try
        {
            PersistenceWriteQueue.WriteBatchFactory =
                static () => throw new InvalidOperationException("no persistence backend");

            using PersistenceWriteQueue queue = PersistenceWriteQueue.Background();
            TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(queue.EnqueueRead(static () => throw new InvalidOperationException("read failed")));
            Assert.True(queue.EnqueueRead(() => second.TrySetResult()));

            Task completed = await Task.WhenAny(second.Task, Task.Delay(WaiterTimeout));
            Assert.True(ReferenceEquals(completed, second.Task), "a throwing read kept the next one from running");
            Assert.True(queue.Flush(FlushTimeout));
        }
        finally
        {
            PersistenceWriteQueue.WriteBatchFactory = original;
        }
    }

    // Control, and the second half of the fix: a factory that fails once is retried, so the writes it
    // could not open for are NOT dropped - they commit on the retry, like the in-group lock path.
    [Fact]
    public void ATransientFactoryFailure_IsRetried_AndTheWritesStillCommit()
    {
        Func<IPersistenceWriteBatch> original = PersistenceWriteQueue.WriteBatchFactory;
        try
        {
            int attempts = 0;
            PersistenceWriteQueue.WriteBatchFactory = () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("database momentarily unavailable");
                }

                return new PassThroughBatch();
            };

            using PersistenceWriteQueue queue = PersistenceWriteQueue.Background();
            int ran = 0;
            TaskCompletionSource read = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(queue.Enqueue(() => Interlocked.Increment(ref ran)));
            Assert.True(queue.EnqueueRead(() => read.TrySetResult()));
            Assert.True(queue.Flush(FlushTimeout));

            Assert.Equal(1, Volatile.Read(ref ran));
            Assert.Equal(0L, queue.FailedWrites);
            Assert.Equal(1L, queue.CompletedWrites);
            Assert.True(read.Task.IsCompletedSuccessfully);
            Assert.True(Volatile.Read(ref attempts) >= 2, "the failing factory was not retried");
        }
        finally
        {
            PersistenceWriteQueue.WriteBatchFactory = original;
        }
    }

    // Control: with a healthy factory nothing changes - the writes commit and the read runs after them.
    [Fact]
    public void AHealthyBatch_StillCommitsItsWrites_AndRunsTheReadAfterThem()
    {
        Func<IPersistenceWriteBatch> original = PersistenceWriteQueue.WriteBatchFactory;
        try
        {
            PersistenceWriteQueue.WriteBatchFactory = static () => new PassThroughBatch();

            using PersistenceWriteQueue queue = PersistenceWriteQueue.Background();
            List<string> order = [];
            object sync = new();
            void Note(string what)
            {
                lock (sync)
                {
                    order.Add(what);
                }
            }

            for (int i = 1; i <= 3; i++)
            {
                int index = i;
                Assert.True(queue.Enqueue(() => Note($"write-{index}")));
            }

            Assert.True(queue.EnqueueRead(() => Note("read")));
            Assert.True(queue.Flush(FlushTimeout));

            lock (sync)
            {
                Assert.Equal(["write-1", "write-2", "write-3", "read"], order);
            }

            Assert.Equal(3L, queue.CompletedWrites);
            Assert.Equal(0L, queue.FailedWrites);
        }
        finally
        {
            PersistenceWriteQueue.WriteBatchFactory = original;
        }
    }

    // The queue's own default batch, with nothing to roll back: each write simply runs.
    private sealed class PassThroughBatch : IPersistenceWriteBatch
    {
        public long CommittedTransactions { get; private set; }

        public bool TransactionStartFailed => false;

        public bool TransactionStartBusy => false;

        public void Activate()
        {
        }

        public void Deactivate()
        {
        }

        public void BeginWrite()
        {
        }

        public void CompleteWrite()
        {
        }

        public void RollBackWrite()
        {
        }

        public void Commit() => CommittedTransactions++;

        public void Abandon()
        {
        }

        public void Dispose()
        {
        }
    }
}
