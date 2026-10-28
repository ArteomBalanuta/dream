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
//
// Reads (EnqueueRead) end a group: the writes queued before a read are committed first, and the
// read then runs outside any transaction, so it only ever sees committed rows.
public sealed class PersistenceWriteQueue : IDisposable
{
    // Bounds how long one transaction holds the database's write lock (the checkpoint writer waits
    // for it) and how much one failed commit has to replay.
    public const int MaxWritesPerGroup = 256;

    // How long Dispose lets the worker drain before it drops what is still queued. Generous: a
    // normal shutdown drains in milliseconds; this only bounds a database that stays locked.
    public static readonly TimeSpan DefaultDisposeDrainDeadline = TimeSpan.FromSeconds(30);

    private readonly bool _background;
    private readonly TimeSpan _disposeDrainDeadline;
    private readonly object _sync = new();
    private readonly Queue<QueuedWrite> _pending = new();
    private Task _worker = Task.CompletedTask;
    private bool _running;
    // Set under _sync by Dispose: Enqueue and EnqueueRead refuse new work, so nothing restarts
    // the drained worker.
    private bool _closed;
    // Set by Dispose once the drain deadline passed: the worker fails what is left and stops.
    private volatile bool _cancelled;
    private long _completedWrites;
    private long _failedWrites;
    private long _committedGroups;
    private int _pendingCount;

    private PersistenceWriteQueue(bool background, TimeSpan disposeDrainDeadline)
    {
        _background = background;
        _disposeDrainDeadline = disposeDrainDeadline;
    }

    public static PersistenceWriteQueue Inline() => new(background: false, DefaultDisposeDrainDeadline);

    public static PersistenceWriteQueue Background() => new(background: true, DefaultDisposeDrainDeadline);

    public static PersistenceWriteQueue Background(TimeSpan disposeDrainDeadline)
    {
        if (disposeDrainDeadline < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(disposeDrainDeadline));
        }

        return new PersistenceWriteQueue(background: true, disposeDrainDeadline);
    }

    public bool IsBackground => _background;

    // True once Dispose started; every later Enqueue and EnqueueRead is refused.
    public bool IsClosed
    {
        get
        {
            lock (_sync)
            {
                return _closed;
            }
        }
    }

    public long CompletedWrites => Interlocked.Read(ref _completedWrites);

    // Writes that failed, were refused after Dispose, or were dropped by a Dispose that ran out
    // of drain time.
    public long FailedWrites => Interlocked.Read(ref _failedWrites);

    // Database transactions the worker committed (background mode). A burst of writes queued faster
    // than the worker drains them commits in a few groups instead of one transaction per write.
    public long CommittedGroups => Interlocked.Read(ref _committedGroups);

    // Writes queued but not yet started (always 0 in inline mode). A counter kept beside the queue,
    // so the server tick reads it without taking the writer's lock.
    public int PendingCount => Volatile.Read(ref _pendingCount);

    // False (and counted as failed) when the queue is already disposed.
    public bool Enqueue(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return Enqueue(new QueuedWrite(write, CountsAsWrite: true));
    }

    // Runs a read on the worker, after every write enqueued before it has committed, outside any
    // transaction, so it sees their committed rows and never a group's uncommitted ones (the
    // join-time profile prefetch). It is not counted as a write, and it must handle its own
    // failures; an exception is only logged. Inline mode runs it immediately. False when the queue
    // is already disposed; the read then never runs, and the caller must fail it itself.
    public bool EnqueueRead(Action read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return Enqueue(new QueuedWrite(read, CountsAsWrite: false));
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

    // Closes the queue, then lets the worker drain. A bounded wait that simply returned let the
    // worker keep writing after the runtime and its owner were disposed, so a busy shutdown could
    // land rows in the database long after the server stopped. Group commit keeps a normal drain
    // short; when the database stays locked (every write waits busy_timeout), the drain deadline
    // cancels the worker instead: what is still queued is dropped, counted as failed and logged,
    // and Dispose returns only after the worker has stopped, so nothing writes after it returns.
    public void Dispose()
    {
        Task worker;
        lock (_sync)
        {
            if (_closed && !_running)
            {
                return;
            }

            _closed = true;
            if (!_running)
            {
                return;
            }

            // Closed: no Enqueue can start another worker, so this is the last one.
            worker = _worker;
        }

        if (WaitQuietly(worker, _disposeDrainDeadline))
        {
            return;
        }

        _cancelled = true;
        NetworkDiagnosticsLog.Write(
            "server.persistence.dispose-timeout",
            $"drain deadline {_disposeDrainDeadline.TotalSeconds:0.#}s passed with {PendingCount} queued; dropping the rest");
        // The worker stops after the write it is running (bounded by busy_timeout).
        WaitQuietly(worker, Timeout.InfiniteTimeSpan);
    }

    private static bool WaitQuietly(Task worker, TimeSpan timeout)
    {
        try
        {
            return worker.Wait(timeout);
        }
        catch (AggregateException)
        {
            // DrainPending catches its own errors; a faulted worker has stopped either way.
            return true;
        }
    }

    private bool Enqueue(QueuedWrite write)
    {
        if (!_background)
        {
            lock (_sync)
            {
                if (_closed)
                {
                    RejectClosed(write);
                    return false;
                }
            }

            ExecuteInline(write);
            return true;
        }

        lock (_sync)
        {
            if (_closed)
            {
                RejectClosed(write);
                return false;
            }

            _pending.Enqueue(write);
            Interlocked.Increment(ref _pendingCount);
            if (_running)
            {
                return true;
            }

            _running = true;
            _worker = Task.Run(DrainPending);
        }

        return true;
    }

    private void RejectClosed(QueuedWrite write)
    {
        if (write.CountsAsWrite)
        {
            Interlocked.Increment(ref _failedWrites);
        }

        NetworkDiagnosticsLog.Write(
            "server.persistence.rejected",
            write.CountsAsWrite ? "write queued after dispose" : "read queued after dispose");
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
                    if (_cancelled)
                    {
                        DropPendingLocked();
                        _running = false;
                        batch.Abandon();
                        return;
                    }

                    // A read is taken alone; writes are taken up to the next read.
                    if (_pending.Count > 0 && !_pending.Peek().CountsAsWrite)
                    {
                        group.Add(_pending.Dequeue());
                    }
                    else
                    {
                        while (_pending.Count > 0
                            && group.Count < MaxWritesPerGroup
                            && _pending.Peek().CountsAsWrite)
                        {
                            group.Add(_pending.Dequeue());
                        }
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
                    if (group.Count == 1 && !group[0].CountsAsWrite)
                    {
                        RunRead(group[0]);
                    }
                    else
                    {
                        RunGroup(batch, group);
                    }
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

    // Called under _sync once the drain deadline passed.
    private void DropPendingLocked()
    {
        int dropped = 0;
        while (_pending.Count > 0)
        {
            if (_pending.Dequeue().CountsAsWrite)
            {
                dropped++;
            }
        }

        Interlocked.Exchange(ref _pendingCount, 0);
        if (dropped > 0)
        {
            Interlocked.Add(ref _failedWrites, dropped);
        }

        NetworkDiagnosticsLog.Write("server.persistence.dropped", $"writes={dropped} reason=dispose-deadline");
    }

    // Outside any transaction: the group before it has committed (or failed), and the repositories
    // open their own connection because no batch is active on this thread while the read runs.
    private static void RunRead(QueuedWrite read)
    {
        SqliteWriteBatch? batch = SqliteWriteBatch.Current;
        SqliteWriteBatch.Deactivate();
        try
        {
            read.Action();
        }
        catch (Exception ex)
        {
            NetworkDiagnosticsLog.Write("server.persistence.error", $"read failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            batch?.Activate();
        }
    }

    // One transaction for the whole group. If the commit itself fails (the transaction is lost,
    // not one write), every write is rolled back with it, so each is replayed in a group of its
    // own; the repositories' writes are upserts and deletes, so a replay lands the same rows.
    // When the transaction cannot even start (the database stays locked past busy_timeout), the
    // rest of the group fails at once instead of waiting busy_timeout once per write. Returns false
    // in that case, so a replay loop stops too.
    private bool RunGroup(SqliteWriteBatch batch, List<QueuedWrite> group)
    {
        bool[]? failed = null;
        bool transactionStartFailed = false;
        try
        {
            for (int i = 0; i < group.Count; i++)
            {
                if (_cancelled)
                {
                    failed ??= new bool[group.Count];
                    FailRemaining(group, i, failed, "dispose-deadline", exception: null);
                    break;
                }

                batch.BeginWrite();
                try
                {
                    group[i].Action();
                    batch.CompleteWrite();
                }
                catch (Exception ex)
                {
                    transactionStartFailed = batch.TransactionStartFailed;
                    batch.RollBackWrite();
                    failed ??= new bool[group.Count];
                    failed[i] = true;
                    ReportFailure(group[i], ex);
                    if (transactionStartFailed)
                    {
                        // The writes before it (if any) still commit below.
                        FailRemaining(group, i + 1, failed, "transaction-start-failed", ex);
                        break;
                    }
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

                return true;
            }

            for (int i = 0; i < group.Count; i++)
            {
                if (failed != null && failed[i])
                {
                    continue;
                }

                if (_cancelled)
                {
                    failed ??= new bool[group.Count];
                    FailRemaining(group, i, failed, "dispose-deadline", exception: null);
                    break;
                }

                if (!RunGroup(batch, [group[i]]))
                {
                    failed ??= new bool[group.Count];
                    failed[i] = true;
                    FailRemaining(group, i + 1, failed, "transaction-start-failed", exception: null);
                    break;
                }
            }

            return !transactionStartFailed;
        }

        CountCompleted(group, failed);
        return !transactionStartFailed;
    }

    private void CountCompleted(List<QueuedWrite> group, bool[]? failed)
    {
        for (int i = 0; i < group.Count; i++)
        {
            if (group[i].CountsAsWrite && (failed == null || !failed[i]))
            {
                Interlocked.Increment(ref _completedWrites);
            }
        }
    }

    // Marks group[start..] failed without running them, with one log line for all of them.
    private void FailRemaining(List<QueuedWrite> group, int start, bool[]? failed, string reason, Exception? exception)
    {
        int count = 0;
        for (int i = start; i < group.Count; i++)
        {
            if (failed != null)
            {
                if (failed[i])
                {
                    continue;
                }

                failed[i] = true;
            }

            if (group[i].CountsAsWrite)
            {
                Interlocked.Increment(ref _failedWrites);
                count++;
            }
        }

        if (count > 0)
        {
            NetworkDiagnosticsLog.Write(
                "server.persistence.dropped",
                exception == null
                    ? $"writes={count} reason={reason}"
                    : $"writes={count} reason={reason} {exception.GetType().Name}: {exception.Message}");
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
