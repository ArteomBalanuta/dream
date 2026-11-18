using Dream.Multiplayer.Diagnostics;

namespace Dream.Simulation.Persistence;

// Ordered queue for the runtime's immediate persistence writes (placements, fractures, enemy
// spawn/death, profile and inventory saves). In background mode one worker runs them in
// enqueue order, so SQLite connection setup and fsync no longer happen on the simulation tick.
// Inline mode runs each write immediately; tests and tools that read the repository right
// after an action use it.
//
// Group commit: the worker takes every write queued so far (up to MaxWritesPerGroup) and runs them
// inside one IPersistenceWriteBatch (the server's SqliteWriteBatch), so the SQLite repositories they
// call share one open connection and one transaction, with a savepoint per write. A failing write
// is rolled back to its savepoint and counted as failed; the writes around it still commit. A
// fracture's ten writes cost one commit instead of ten autocommits.
//
// Reads (EnqueueRead) end a group: the writes queued before a read are committed first, and the
// read then runs outside any transaction, so it only ever sees committed rows.
//
// Locked database: when a group's transaction cannot start because another connection holds the
// write lock past busy_timeout (the checkpoint writer, a DB browser, a backup), the writes it did
// not run go back to the head of the queue in order and the worker backs off (100 ms doubling to
// 2 s) before retrying, so a long lock delays writes instead of dropping them. A read queued behind
// them still runs after them. Only once Dispose's drain deadline has passed does a locked database
// fail the rest of the group at once, so shutdown stays bounded.
public sealed class PersistenceWriteQueue : IDisposable
{
    // Bounds how long one transaction holds the database's write lock (the checkpoint writer waits
    // for it) and how much one failed commit has to replay.
    public const int MaxWritesPerGroup = 256;

    // How long Dispose lets the worker drain before it drops what is still queued. Generous: a
    // normal shutdown drains in milliseconds; this only bounds a database that stays locked.
    public static readonly TimeSpan DefaultDisposeDrainDeadline = TimeSpan.FromSeconds(30);

    // Backoff between retries of a group whose transaction could not start on a locked database.
    // Each attempt itself already waits up to busy_timeout for the lock.
    public static readonly TimeSpan InitialLockRetryDelay = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaxLockRetryDelay = TimeSpan.FromSeconds(2);

    // How many times a worker tries to open its write batch before it gives up on the queued work.
    // The waits between the attempts are InitialLockRetryDelay * attempt (100 ms, then 200 ms), so a
    // transient factory failure costs at most ~300 ms and never an unbounded wait.
    public const int BatchOpenAttempts = 3;

    // Creates the batch each background worker runs its groups in. Server persistence installs the
    // SQLite group-commit batch when the assembly loads (SqliteWriteBatch.RegisterAsDefault); with
    // nothing installed each write runs on its own.
    internal static Func<IPersistenceWriteBatch> WriteBatchFactory { get; set; } =
        static () => new PassThroughPersistenceWriteBatch();

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
    private long _lockRetries;
    private int _pendingCount;
    // Environment.TickCount64 (ms) at which the shutdown drain ends; 0 until BeginShutdownDrain.
    private long _drainDeadlineMilliseconds;
    // Mirrors _lockEpisode for other threads (IsLocked).
    private volatile bool _locked;
    // Set once the shutdown drain has seen the database locked (LockSeenDuringShutdownDrain).
    private volatile bool _lockSeenDuringShutdownDrain;
    // Worker-thread state for the current lock episode (database locked until a group runs again).
    private bool _lockEpisode;
    private TimeSpan _lockRetryDelay = InitialLockRetryDelay;
    private Exception? _lockCause;

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

    // Times a group's transaction could not start on a locked database and its unrun writes were
    // put back at the head of the queue to retry (each retry is one busy_timeout wait plus backoff).
    public long LockRetries => Interlocked.Read(ref _lockRetries);

    // Writes queued but not yet started (always 0 in inline mode). A counter kept beside the queue,
    // so the server tick reads it without taking the writer's lock.
    public int PendingCount => Volatile.Read(ref _pendingCount);

    // True while the worker is in a lock episode: a group's transaction could not start because
    // another connection held the write lock past busy_timeout, and no group has run since. A
    // barrier read queued behind the held-back writes cannot complete until the lock is released.
    public bool IsLocked => _locked;

    // How long Dispose drains before it drops what is still queued, measured from
    // BeginShutdownDrain (or from Dispose, when nothing started the drain earlier).
    public TimeSpan DisposeDrainDeadline => _disposeDrainDeadline;

    // True once BeginShutdownDrain (or Dispose) started the drain deadline.
    public bool IsShutdownDrainStarted => Interlocked.Read(ref _drainDeadlineMilliseconds) != 0;

    // Starts the shutdown drain deadline (idempotent; the first call wins). Everything shutdown
    // waits on before Dispose (the background checkpoint writer's barrier wait, the final
    // checkpoint) shares this one deadline with Dispose's drain, so their waits add no time on top
    // of it: on a locked database the whole shutdown is bounded by DisposeDrainDeadline plus the
    // busy_timeout of the attempt in flight, instead of each stage waiting out its own timeout.
    public void BeginShutdownDrain()
    {
        long now = Environment.TickCount64;
        long deadline = now + (long)Math.Min(_disposeDrainDeadline.TotalMilliseconds, long.MaxValue / 4);
        Interlocked.CompareExchange(ref _drainDeadlineMilliseconds, Math.Max(1, deadline), 0);
        if (_locked)
        {
            _lockSeenDuringShutdownDrain = true;
        }
    }

    // True once the shutdown drain has seen a lock episode: the queue was in one when the drain
    // started, or a group's transaction could not start on a locked database after it started
    // (retried, or failed fast past the deadline). Shutdown work that runs outside the queue's
    // drain deadline (the runtime's final checkpoint fallback) is skipped then, so a held lock
    // still bounds shutdown by the deadline; on a database that was never locked, a backlog that
    // merely outlasts the deadline does not cost it the final checkpoint.
    public bool LockSeenDuringShutdownDrain => _lockSeenDuringShutdownDrain;

    // Time left until the shutdown drain deadline: DisposeDrainDeadline before the drain started,
    // never negative.
    public TimeSpan RemainingDrainTime
    {
        get
        {
            long deadline = Interlocked.Read(ref _drainDeadlineMilliseconds);
            if (deadline == 0)
            {
                return _disposeDrainDeadline;
            }

            long remaining = deadline - Environment.TickCount64;
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(remaining);
        }
    }

    // False (and counted as failed) when the queue is already disposed.
    public bool Enqueue(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return Enqueue(new QueuedWrite(write, CountsAsWrite: true));
    }

    // Runs a read on the worker, after every write enqueued before it has run (committed, or failed
    // and counted in FailedWrites; a lock only delays them, see RequeueAndBackOff), outside any
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
    // The drain deadline runs from BeginShutdownDrain when the owner started it earlier, so the
    // waits before Dispose and the drain share one deadline.
    public void Dispose()
    {
        Task worker;
        BeginShutdownDrain();
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

        if (WaitQuietly(worker, RemainingDrainTime))
        {
            return;
        }

        lock (_sync)
        {
            _cancelled = true;
            // Wakes a worker backing off from a locked database.
            Monitor.PulseAll(_sync);
        }

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
        List<QueuedWrite> requeue = [];
        // Opening the batch is part of the worker's run: a throw here used to escape before the
        // try below, leaving _running set with no worker, so every later Enqueue queued silently
        // into a queue nothing drains and Flush waited on a faulted task. Fail what is queued and
        // clear _running instead, so the next Enqueue starts a fresh worker.
        //
        // A factory failure is usually transient (the file momentarily unavailable, a pool exhausted),
        // so TryOpenBatch retries it a bounded number of times before the queue gives up, the way the
        // in-group lock path requeues and backs off. What it does drop is not dropped silently: the
        // queued READS are run here, because a read's action is the only thing that ever completes its
        // caller (the join prefetch, a checkpoint barrier, an inventory settle), and a caller that
        // EnqueueRead told "true" would otherwise wait for ever.
        IPersistenceWriteBatch? opened = TryOpenBatch(out Exception? openError);
        if (opened == null)
        {
            List<QueuedWrite>? droppedReads;
            lock (_sync)
            {
                droppedReads = DropPendingLocked("batch-unavailable", collectReads: true);
                _running = false;
            }

            NetworkDiagnosticsLog.Write(
                "server.persistence.error",
                $"write batch unavailable after {BatchOpenAttempts} attempts: {openError?.GetType().Name}: {openError?.Message}");
            RunDroppedReads(droppedReads);
            return;
        }

        using IPersistenceWriteBatch batch = opened;
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

                requeue.Clear();
                bool isRead = group.Count == 1 && !group[0].CountsAsWrite;
                try
                {
                    if (isRead)
                    {
                        RunRead(batch, group[0]);
                    }
                    else
                    {
                        RunGroup(batch, group, requeue);
                    }
                }
                catch (Exception ex)
                {
                    // RunGroup reports per-write failures itself; this only keeps the worker alive
                    // (a dead worker would leave _running set and Flush waiting) if cleanup fails.
                    batch.Abandon();
                    NetworkDiagnosticsLog.Write("server.persistence.error", $"group failed: {ex.GetType().Name}: {ex.Message}");
                }

                if (requeue.Count > 0)
                {
                    // Close this queue's connection while it backs off, so it holds nothing open
                    // on the locked database.
                    batch.Abandon();
                    RequeueAndBackOff(requeue);
                }
                else if (!isRead)
                {
                    EndLockEpisode();
                }
            }
        }
        finally
        {
            batch.Deactivate();
        }
    }

    // Puts the writes a locked database kept from running back at the head of the queue, in their
    // order and ahead of anything (a read included) queued behind them, then waits out the backoff.
    // Logs once per lock episode, not once per retry.
    private void RequeueAndBackOff(List<QueuedWrite> requeue)
    {
        Interlocked.Increment(ref _lockRetries);
        NoteLockDuringShutdownDrain();
        if (!_lockEpisode)
        {
            _lockEpisode = true;
            _locked = true;
            NetworkDiagnosticsLog.Write(
                "server.persistence.locked",
                $"database locked; retrying writes={requeue.Count} with backoff up to {MaxLockRetryDelay.TotalSeconds:0.#}s: {_lockCause?.GetType().Name}: {_lockCause?.Message}");
        }

        TimeSpan delay = _lockRetryDelay;
        _lockRetryDelay = TimeSpan.FromTicks(Math.Min(MaxLockRetryDelay.Ticks, _lockRetryDelay.Ticks * 2));
        lock (_sync)
        {
            int behind = _pending.Count;
            for (int i = 0; i < requeue.Count; i++)
            {
                _pending.Enqueue(requeue[i]);
            }

            // Rotate what was already queued behind the requeued writes back to the end.
            for (int i = 0; i < behind; i++)
            {
                _pending.Enqueue(_pending.Dequeue());
            }

            Interlocked.Add(ref _pendingCount, requeue.Count);
            if (!_cancelled)
            {
                // Dispose pulses on cancel; a spurious wake only retries early.
                Monitor.Wait(_sync, delay);
            }
        }
    }

    private void NoteLockDuringShutdownDrain()
    {
        if (IsShutdownDrainStarted)
        {
            _lockSeenDuringShutdownDrain = true;
        }
    }

    private void EndLockEpisode()
    {
        if (!_lockEpisode)
        {
            return;
        }

        _lockEpisode = false;
        _locked = false;
        _lockRetryDelay = InitialLockRetryDelay;
        _lockCause = null;
        NetworkDiagnosticsLog.Write("server.persistence.locked", $"database unlocked; lockRetries={LockRetries}");
    }

    // Opens the batch this worker runs its groups in, retrying a failing factory BatchOpenAttempts
    // times with the lock backoff (100 ms doubling, so at most ~300 ms in all before it gives up).
    // Null, with the last error, when every attempt failed.
    private IPersistenceWriteBatch? TryOpenBatch(out Exception? error)
    {
        error = null;
        for (int attempt = 1; attempt <= BatchOpenAttempts; attempt++)
        {
            IPersistenceWriteBatch? opened = null;
            try
            {
                opened = WriteBatchFactory();
                opened.Activate();
                return opened;
            }
            catch (Exception ex)
            {
                error = ex;
                try
                {
                    opened?.Dispose();
                }
                catch (Exception disposeError)
                {
                    NetworkDiagnosticsLog.Write(
                        "server.persistence.error",
                        $"write batch dispose failed: {disposeError.GetType().Name}: {disposeError.Message}");
                }
            }

            if (attempt == BatchOpenAttempts || _cancelled)
            {
                break;
            }

            lock (_sync)
            {
                if (_cancelled)
                {
                    break;
                }

                // Dispose pulses on cancel, so a shutdown is never held up by the full backoff.
                Monitor.Wait(_sync, TimeSpan.FromTicks(InitialLockRetryDelay.Ticks * attempt));
            }
        }

        return null;
    }

    // Runs the reads a failed batch dropped, outside any batch (the repositories open their own
    // connection), so their callers learn instead of waiting for ever. Each is guarded, exactly as
    // RunRead guards a read it runs normally, so one throwing read still lets the others complete.
    // The worker has already cleared _running, so a slow read here delays nothing: the next Enqueue
    // starts a fresh worker beside it.
    private static void RunDroppedReads(List<QueuedWrite>? reads)
    {
        if (reads == null)
        {
            return;
        }

        for (int i = 0; i < reads.Count; i++)
        {
            try
            {
                reads[i].Action();
            }
            catch (Exception ex)
            {
                NetworkDiagnosticsLog.Write(
                    "server.persistence.error",
                    $"dropped read failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Called under _sync once the drain deadline passed, or when no batch could be opened. With
    // collectReads the dropped reads are returned instead of discarded, so the caller can run them.
    private List<QueuedWrite>? DropPendingLocked(string reason = "dispose-deadline", bool collectReads = false)
    {
        int dropped = 0;
        List<QueuedWrite>? reads = null;
        while (_pending.Count > 0)
        {
            QueuedWrite queued = _pending.Dequeue();
            if (queued.CountsAsWrite)
            {
                dropped++;
            }
            else if (collectReads)
            {
                (reads ??= []).Add(queued);
            }
        }

        Interlocked.Exchange(ref _pendingCount, 0);
        if (dropped > 0)
        {
            Interlocked.Add(ref _failedWrites, dropped);
        }

        NetworkDiagnosticsLog.Write(
            "server.persistence.dropped",
            $"writes={dropped} reads={reads?.Count ?? 0} reason={reason}");
        return reads;
    }

    // Outside any transaction: the group before it has committed (or failed), and the repositories
    // open their own connection because no batch is active on this thread while the read runs.
    private static void RunRead(IPersistenceWriteBatch batch, QueuedWrite read)
    {
        batch.Deactivate();
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
            batch.Activate();
        }
    }

    // One transaction for the whole group. If the commit itself fails (the transaction is lost,
    // not one write), every write is rolled back with it, so each is replayed in a group of its
    // own; nothing of the lost group landed, so the replay applies each write once (wallet deltas
    // included). A write that throws (a guarded slot upsert or sell destroy) rolls back its savepoint.
    // When the transaction cannot start because the database stays locked past busy_timeout, the
    // write that hit it and every write after it go to requeue unrun (not counted), and the caller
    // puts them back at the head of the queue; the writes before it still commit. Once the dispose
    // deadline has passed, or when the transaction fails to start for another reason, the rest of
    // the group fails at once instead of waiting busy_timeout once per write.
    private GroupOutcome RunGroup(IPersistenceWriteBatch batch, List<QueuedWrite> group, List<QueuedWrite> requeue)
    {
        bool[]? failed = null;
        bool transactionStartFailed = false;
        // First write left unrun for a retry; group.Count when none.
        int retryFrom = group.Count;
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
                    if (transactionStartFailed && batch.TransactionStartBusy)
                    {
                        NoteLockDuringShutdownDrain();
                    }

                    bool retry = transactionStartFailed && batch.TransactionStartBusy && !_cancelled;
                    batch.RollBackWrite();
                    if (retry)
                    {
                        // The writes before it (if any) still commit below.
                        _lockCause = ex;
                        retryFrom = i;
                        break;
                    }

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
                if (retryFrom == 0)
                {
                    requeue.Add(group[0]);
                    return GroupOutcome.Requeued;
                }

                if (failed == null)
                {
                    ReportFailure(group[0], ex);
                }

                return transactionStartFailed ? GroupOutcome.TransactionStartFailed : GroupOutcome.Ran;
            }

            // The writes that ran before the lock (or all of them) were lost with the transaction.
            for (int i = 0; i < retryFrom; i++)
            {
                if (failed != null && failed[i])
                {
                    continue;
                }

                if (_cancelled)
                {
                    failed ??= new bool[group.Count];
                    FailRemaining(group, i, failed, "dispose-deadline", exception: null);
                    return GroupOutcome.Ran;
                }

                GroupOutcome replay = RunGroup(batch, [group[i]], requeue);
                if (replay == GroupOutcome.Requeued)
                {
                    // group[i] is already in requeue; the rest of the group follows it in order.
                    AppendUnfailed(group, i + 1, failed, requeue);
                    return GroupOutcome.Requeued;
                }

                if (replay == GroupOutcome.TransactionStartFailed)
                {
                    failed ??= new bool[group.Count];
                    failed[i] = true;
                    FailRemaining(group, i + 1, failed, "transaction-start-failed", exception: null);
                    return GroupOutcome.TransactionStartFailed;
                }
            }

            if (retryFrom < group.Count)
            {
                AppendUnfailed(group, retryFrom, failed, requeue);
                return GroupOutcome.Requeued;
            }

            return transactionStartFailed ? GroupOutcome.TransactionStartFailed : GroupOutcome.Ran;
        }

        CountCompleted(group, failed, retryFrom);
        if (retryFrom < group.Count)
        {
            AppendUnfailed(group, retryFrom, failed, requeue);
            return GroupOutcome.Requeued;
        }

        return transactionStartFailed ? GroupOutcome.TransactionStartFailed : GroupOutcome.Ran;
    }

    private static void AppendUnfailed(List<QueuedWrite> group, int start, bool[]? failed, List<QueuedWrite> requeue)
    {
        for (int i = start; i < group.Count; i++)
        {
            if (failed == null || !failed[i])
            {
                requeue.Add(group[i]);
            }
        }
    }

    private void CountCompleted(List<QueuedWrite> group, bool[]? failed, int end)
    {
        for (int i = 0; i < end; i++)
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

    private enum GroupOutcome
    {
        Ran,
        // Its transaction could not start on a locked database; the unrun writes are in requeue.
        Requeued,
        // Its transaction could not start and the rest of the group was failed (fail-fast).
        TransactionStartFailed,
    }
}
