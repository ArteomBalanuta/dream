namespace Dream.Simulation.Persistence;

// Group commit for PersistenceWriteQueue's worker: the writes of one group run inside one
// transaction, each in its own savepoint. Server persistence supplies the SQLite batch
// (SqliteWriteBatch) through PersistenceWriteQueue.WriteBatchFactory; Simulation only sees this port.
//
// Not thread-safe: one worker thread owns a batch, and the ambient slot it activates is per thread.
public interface IPersistenceWriteBatch : IDisposable
{
    // Transactions committed by Commit, for tests and diagnostics.
    long CommittedTransactions { get; }

    // Set when the group's transaction could not start; the queue then fails the rest of the
    // group. Cleared by Commit and Abandon.
    bool TransactionStartFailed { get; }

    // Set with TransactionStartFailed when the cause was a transient lock held by another
    // connection; the queue then puts the unrun writes back and retries. Cleared by Commit and Abandon.
    bool TransactionStartBusy { get; }

    // Makes this batch the ambient one on the calling thread until Deactivate.
    void Activate();

    // Clears the ambient batch on the calling thread.
    void Deactivate();

    // Starts one queued write.
    void BeginWrite();

    // Keeps the write's changes in the transaction.
    void CompleteWrite();

    // Undoes only the write's own changes.
    void RollBackWrite();

    // Commits every open transaction.
    void Commit();

    // Rolls back whatever is open and releases the batch's connections.
    void Abandon();
}

// The batch used when no persistence backend installed one: each write simply runs, with no
// transaction around it.
internal sealed class PassThroughPersistenceWriteBatch : IPersistenceWriteBatch
{
    public long CommittedTransactions => 0;

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
