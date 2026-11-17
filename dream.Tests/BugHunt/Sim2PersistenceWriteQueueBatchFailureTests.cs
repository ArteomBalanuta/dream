using Dream.Simulation.Persistence;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-SIM2-2: WriteBatchFactory() and batch.Activate() ran outside DrainPending's try, so a throw
// there escaped the worker with _running still true: every later Enqueue queued silently into a
// queue nothing drained, Flush waited on a faulted Task, and Dispose dropped the writes without
// counting or logging them.
//
// WriteBatchFactory is process-wide state, so this class runs alone (see TimingSensitiveCollection).
[Collection(TimingSensitiveCollection.Name)]
public class Sim2PersistenceWriteQueueBatchFailureTests
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void AWriteBatchThatCannotBeCreated_FailsTheQueuedWrites_AndLeavesTheQueueUsable()
    {
        Func<IPersistenceWriteBatch> original = PersistenceWriteQueue.WriteBatchFactory;
        try
        {
            PersistenceWriteQueue.WriteBatchFactory =
                static () => throw new InvalidOperationException("no persistence backend");

            using PersistenceWriteQueue queue = PersistenceWriteQueue.Background();
            int ran = 0;
            Assert.True(queue.Enqueue(() => Interlocked.Increment(ref ran)));

            // Before the fix this threw an AggregateException from the faulted worker Task and
            // never returned true, because _running stayed set forever.
            Assert.True(queue.Flush(FlushTimeout));

            Assert.Equal(0, Volatile.Read(ref ran));
            Assert.Equal(0L, queue.CompletedWrites);
            Assert.Equal(1L, queue.FailedWrites);
            Assert.Equal(0, queue.PendingCount);

            // The queue is not wedged: a later write is started by a fresh worker and fails loudly
            // too, rather than piling up uncounted behind a worker that no longer exists.
            Assert.True(queue.Enqueue(() => Interlocked.Increment(ref ran)));
            Assert.True(queue.Flush(FlushTimeout));
            Assert.Equal(2L, queue.FailedWrites);
            Assert.Equal(0, queue.PendingCount);
        }
        finally
        {
            PersistenceWriteQueue.WriteBatchFactory = original;
        }
    }

    [Fact]
    public void AWriteBatchWhoseActivateThrows_IsTreatedTheSameWay()
    {
        Func<IPersistenceWriteBatch> original = PersistenceWriteQueue.WriteBatchFactory;
        try
        {
            PersistenceWriteQueue.WriteBatchFactory = static () => new ThrowingActivateBatch();

            using PersistenceWriteQueue queue = PersistenceWriteQueue.Background();
            Assert.True(queue.Enqueue(static () => { }));
            Assert.True(queue.Flush(FlushTimeout));

            Assert.Equal(0L, queue.CompletedWrites);
            Assert.Equal(1L, queue.FailedWrites);
        }
        finally
        {
            PersistenceWriteQueue.WriteBatchFactory = original;
        }
    }

    private sealed class ThrowingActivateBatch : IPersistenceWriteBatch
    {
        public long CommittedTransactions => 0;

        public bool TransactionStartFailed => false;

        public bool TransactionStartBusy => false;

        public void Activate() => throw new InvalidOperationException("cannot open a connection");

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

        public void Commit()
        {
        }

        public void Abandon()
        {
        }

        public void Dispose()
        {
        }
    }
}
