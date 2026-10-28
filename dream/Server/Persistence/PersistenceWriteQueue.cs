using Dream.Multiplayer.Diagnostics;

namespace Dream.Server.Persistence;

// Ordered queue for the runtime's immediate persistence writes (placements, fractures, enemy
// spawn/death, profile and inventory saves). In background mode one worker runs them in
// enqueue order, so SQLite connection setup and fsync no longer happen on the simulation tick.
// Inline mode runs each write immediately; tests and tools that read the repository right
// after an action use it.
//
// Group commit: the worker takes every write queued so far (up to MaxWritesPerGroup) and runs them
// inside one SqliteWriteBatch, so the SQLite repositories they call share one open connection and
// one transaction, with a savepoint per write. A failing write is rolled back to its savepoint and
// counted as failed; the writes around it still commit. A fracture's ten writes cost one commit
// instead of ten autocommits.
public sealed class PersistenceWriteQueue : IDisposable
{
    // Bounds how long one transaction holds the database's write lock (the checkpoint writer waits
    // for it) and how much one failed commit has to replay.
    public const int MaxWritesPerGroup = 256;

    private readonly bool _background;
    private readonly object _sync = new();
    private readonly Queue<QueuedWrite> _pending = new();
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private long _completedWrites;
    private long _failedWrites;
    private long _committedGroups;
    private int _pendingCount;

    private PersistenceWriteQueue(bool background)
    {
        _background = background;
    }

    public static PersistenceWriteQueue Inline() => new(background: false);

    public static PersistenceWriteQueue Background() => new(background: true);

    public bool IsBackground => _background;

    public long CompletedWrites => Interlocked.Read(ref _completedWrites);

    public long FailedWrites => Interlocked.Read(ref _failedWrites);

    // Database transactions the worker committed (background mode). A burst of writes queued faster
    // than the worker drains them commits in a few groups instead of one transaction per write.
    public long CommittedGroups => Interlocked.Read(ref _committedGroups);

    // Writes queued but not yet started (always 0 in inline mode). A counter kept beside the queue,
    // so the server tick reads it without taking the writer's lock.
    public int PendingCount => Volatile.Read(ref _pendingCount);

    public void Enqueue(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);
        Enqueue(new QueuedWrite(write, CountsAsWrite: true));
    }

    // Runs a read on the worker, after every write enqueued before it, so it sees their rows (the
    // join-time profile prefetch). It is not counted as a write, and it must handle its own
    // failures; an exception is only logged. Inline mode runs it immediately.
    public void EnqueueRead(Action read)
    {
        ArgumentNullException.ThrowIfNull(read);
        Enqueue(new QueuedWrite(read, CountsAsWrite: false));
    }

    // Waits until every write enqueued so far has run. Read paths call this before reading
    // rows that a queued write may still change.
    public bool Flush(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task worker;
            lock (_sync)
            {
                if (!_running)
                {
                    return true;
                }

                worker = _worker;
            }

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !worker.Wait(remaining))
            {
                return false;
            }
        }
    }

    // Runs every queued write before returning. A bounded wait here let the worker keep writing
    // after the runtime and its owner were disposed, so a busy shutdown (a fracture queues one
    // write per piece) could land rows in the database long after the server stopped. Group commit
    // keeps that drain short.
    public void Dispose()
    {
        while (true)
        {
            Task worker;
            lock (_sync)
            {
                if (!_running)
                {
                    return;
                }

                worker = _worker;
            }

            worker.Wait();
        }
    }

    private void Enqueue(QueuedWrite write)
    {
        if (!_background)
        {
            ExecuteInline(write);
            return;
        }

        lock (_sync)
        {
            _pending.Enqueue(write);
            Interlocked.Increment(ref _pendingCount);
            if (_running)
            {
                return;
            }

            _running = true;
            _worker = Task.Run(DrainPending);
        }
    }

    private void DrainPending()
    {
        List<QueuedWrite> group = new(Math.Min(MaxWritesPerGroup, 64));
        using SqliteWriteBatch batch = new();
        batch.Activate();
        try
        {
            while (true)
            {
                group.Clear();
                lock (_sync)
                {
                    while (_pending.Count > 0 && group.Count < MaxWritesPerGroup)
                    {
                        group.Add(_pending.Dequeue());
                    }

                    Interlocked.Add(ref _pendingCount, -group.Count);
                }

                if (group.Count == 0)
                {
                    // Idle: close the connection before reporting the queue drained, so Flush and
                    // Dispose return with no connection of this queue left open on the database.
                    batch.Abandon();
                    lock (_sync)
                    {
                        if (_pending.Count == 0)
                        {
                            _running = false;
                            return;
                        }
                    }

                    continue;
                }

                try
                {
                    RunGroup(batch, group);
                }
                catch (Exception ex)
                {
                    // RunGroup reports per-write failures itself; this only keeps the worker alive
                    // (a dead worker would leave _running set and Flush waiting) if cleanup fails.
                    batch.Abandon();
                    NetworkDiagnosticsLog.Write("server.persistence.error", $"group failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            SqliteWriteBatch.Deactivate();
        }
    }

    // One transaction for the whole group. If the commit itself fails (the transaction is lost,
    // not one write), every write is rolled back with it, so each is replayed in a group of its
    // own; the repositories' writes are upserts and deletes, so a replay lands the same rows.
    private void RunGroup(SqliteWriteBatch batch, List<QueuedWrite> group)
    {
        bool[]? failed = null;
        try
        {
            for (int i = 0; i < group.Count; i++)
            {
                batch.BeginWrite();
                try
                {
                    group[i].Action();
                    batch.CompleteWrite();
                }
                catch (Exception ex)
                {
                    batch.RollBackWrite();
                    failed ??= new bool[group.Count];
                    failed[i] = true;
                    ReportFailure(group[i], ex);
                }
            }

            long committedBefore = batch.CommittedTransactions;
            batch.Commit();
            Interlocked.Add(ref _committedGroups, batch.CommittedTransactions - committedBefore);
        }
        catch (Exception ex)
        {
            batch.Abandon();
            NetworkDiagnosticsLog.Write(
                "server.persistence.error",
                $"group of {group.Count} not committed, replaying one by one: {ex.GetType().Name}: {ex.Message}");
            if (group.Count == 1)
            {
                if (failed == null)
                {
                    ReportFailure(group[0], ex);
                }

                return;
            }

            for (int i = 0; i < group.Count; i++)
            {
                if (failed == null || !failed[i])
                {
                    RunGroup(batch, [group[i]]);
                }
            }

            return;
        }

        for (int i = 0; i < group.Count; i++)
        {
            if (group[i].CountsAsWrite && (failed == null || !failed[i]))
            {
                Interlocked.Increment(ref _completedWrites);
            }
        }
    }

    private void ReportFailure(QueuedWrite write, Exception ex)
    {
        if (write.CountsAsWrite)
        {
            Interlocked.Increment(ref _failedWrites);
        }

        NetworkDiagnosticsLog.Write("server.persistence.error", $"{ex.GetType().Name}: {ex.Message}");
    }

    private void ExecuteInline(QueuedWrite write)
    {
        write.Action();
        if (write.CountsAsWrite)
        {
            Interlocked.Increment(ref _completedWrites);
        }
    }

    private readonly record struct QueuedWrite(Action Action, bool CountsAsWrite);
}
