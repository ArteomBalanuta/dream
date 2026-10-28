using System.Diagnostics;
using Dream.Multiplayer.Diagnostics;
using Dream.Server.Persistence;

namespace Dream.Server;

// Runs checkpoint writes on one background worker so database I/O never blocks the
// authoritative tick. Writes are serialized. A checkpoint that arrives while another is still
// waiting is merged into it (PersistenceCheckpoint.Merge): the last write wins per key and
// tombstones are kept, so a writer that falls behind writes one group that leaves the database
// where writing every checkpoint in order would, instead of dropping the older one. A failed
// write is kept and merged under the next checkpoint, so its changes are not lost either.
public sealed class BackgroundCheckpointWriter : IDisposable
{
    // How long a write waits for its checkpoint's immediate-writes barrier before going ahead
    // anyway (the queue is closed, or stuck). At shutdown the wait is also bounded by the write
    // queue's drain deadline; see WaitForImmediateWrites.
    public static readonly TimeSpan ImmediateWritesWaitTimeout = TimeSpan.FromSeconds(10);

    // Poll interval for a barrier wait that can be cut short by shutdown (a lock episode or the
    // drain deadline). Only a write whose barrier is still pending polls.
    private const int BarrierPollMilliseconds = 50;

    private readonly IPersistenceCheckpointWriter _writer;
    // The runtime's immediate-write queue, whose barrier reads the checkpoints carry. Null: the
    // barrier wait is bounded only by ImmediateWritesWaitTimeout.
    private readonly PersistenceWriteQueue? _immediateWrites;
    private volatile bool _disposing;
    private long _deferredAtShutdown;
    private readonly object _sync = new();
    private PersistenceCheckpoint? _pending;
    // The last failed checkpoint (merged with any later failures), written again under the next
    // enqueued checkpoint.
    private PersistenceCheckpoint? _failed;
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private long _completedWrites;
    private long _failedWrites;
    private long _supersededCheckpoints;
    private long _mergedCheckpoints;
    private long _enqueuedCheckpoints;
    private long _incrementalWrites;
    private long _fullWrites;
    private long _lastWriteRows;
    private long _totalWrittenRows;
    // Stopwatch ticks spent in IPersistenceCheckpointWriter.Write, successful or not.
    private long _lastWriteTicks;
    private long _maxWriteTicks;
    private long _totalWriteTicks;
    private long _timedWrites;

    public BackgroundCheckpointWriter(IPersistenceCheckpointWriter writer)
        : this(writer, immediateWrites: null)
    {
    }

    public BackgroundCheckpointWriter(IPersistenceCheckpointWriter writer, PersistenceWriteQueue? immediateWrites)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _immediateWrites = immediateWrites;
    }

    // Checkpoints Dispose left unwritten because their barrier could not complete before the
    // shutdown drain deadline (the database was locked, or a backlog outlasted the deadline). The
    // runtime's final full checkpoint, queued behind the same immediate writes, covers them: on a
    // database the drain never saw locked, it is written even when the drain drops it (see
    // WorldSessionRuntime.WriteFinalCheckpoint).
    public long DeferredAtShutdownCheckpoints => Interlocked.Read(ref _deferredAtShutdown);

    public long CompletedWrites => Interlocked.Read(ref _completedWrites);

    public long FailedWrites => Interlocked.Read(ref _failedWrites);

    // Checkpoints that arrived while another was still waiting, and were merged into it.
    public long SupersededCheckpoints => Interlocked.Read(ref _supersededCheckpoints);

    // The part of SupersededCheckpoints whose waiting checkpoint held rows or tombstones.
    public long MergedCheckpoints => Interlocked.Read(ref _mergedCheckpoints);

    // Every Enqueue call; the server compares it across a tick to see whether that tick captured one.
    public long EnqueuedCheckpoints => Interlocked.Read(ref _enqueuedCheckpoints);

    // Successful writes by kind (a merge of incremental checkpoints into a full one counts as full).
    public long IncrementalWrites => Interlocked.Read(ref _incrementalWrites);

    public long FullWrites => Interlocked.Read(ref _fullWrites);

    // Rows (PersistenceCheckpoint.RowCount) in the last successful write, and in all of them.
    public long LastWriteRows => Interlocked.Read(ref _lastWriteRows);

    public long TotalWrittenRows => Interlocked.Read(ref _totalWrittenRows);

    public double LastWriteMilliseconds => ServerTickMetrics.ToMilliseconds(Interlocked.Read(ref _lastWriteTicks));

    public double MaxWriteMilliseconds => ServerTickMetrics.ToMilliseconds(Interlocked.Read(ref _maxWriteTicks));

    public double MeanWriteMilliseconds
    {
        get
        {
            long writes = Interlocked.Read(ref _timedWrites);
            return writes == 0 ? 0d : ServerTickMetrics.ToMilliseconds(Interlocked.Read(ref _totalWriteTicks)) / writes;
        }
    }

    public void Enqueue(PersistenceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        Interlocked.Increment(ref _enqueuedCheckpoints);
        lock (_sync)
        {
            if (_failed != null)
            {
                checkpoint = PersistenceCheckpoint.Merge(_failed, checkpoint);
                _failed = null;
            }

            if (_pending != null)
            {
                _supersededCheckpoints++;
                if (_pending.RowCount != 0 || _pending.TombstoneCount != 0)
                {
                    _mergedCheckpoints++;
                }

                checkpoint = PersistenceCheckpoint.Merge(_pending, checkpoint);
            }

            _pending = checkpoint;
            if (_running)
            {
                return;
            }

            _running = true;
            _worker = Task.Run(DrainPending);
        }
    }

    // Waits until every enqueued checkpoint has been written or the timeout expires.
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

    // Writes the pending checkpoint before returning, so no checkpoint lands in the database after
    // the host that captured it is disposed (see PersistenceWriteQueue.Dispose). The runtime's own
    // Dispose then writes a final full checkpoint, which covers a failed write still held here.
    // Dispose starts the write queue's shutdown drain deadline: a barrier wait that cannot finish
    // within it (a locked database) gives up and leaves the checkpoint to the final one, instead of
    // waiting its own ImmediateWritesWaitTimeout on top of the queue's drain. Starting the deadline
    // here means this write and the queued immediate writes consume it before the final checkpoint
    // runs; the runtime therefore drops the final checkpoint with the deadline only when the drain
    // saw a lock episode (PersistenceWriteQueue.LockSeenDuringShutdownDrain), and otherwise writes
    // it after the queue stopped.
    public void Dispose()
    {
        _immediateWrites?.BeginShutdownDrain();
        _disposing = true;
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

    private void DrainPending()
    {
        while (true)
        {
            PersistenceCheckpoint? next;
            lock (_sync)
            {
                next = _pending;
                _pending = null;
                if (next == null)
                {
                    _running = false;
                    return;
                }
            }

            if (!WaitForImmediateWrites(next))
            {
                Interlocked.Increment(ref _deferredAtShutdown);
                NetworkDiagnosticsLog.Write(
                    "server.checkpoint.deferred",
                    $"immediate writes still pending at shutdown (locked={_immediateWrites?.IsLocked}); left to the final checkpoint rows={next.RowCount}");
                lock (_sync)
                {
                    _failed = _failed == null ? next : PersistenceCheckpoint.Merge(_failed, next);
                }

                continue;
            }

            long started = Stopwatch.GetTimestamp();
            try
            {
                _writer.Write(next);
                Interlocked.Increment(ref _completedWrites);
                if (next.IsIncremental)
                {
                    Interlocked.Increment(ref _incrementalWrites);
                }
                else
                {
                    Interlocked.Increment(ref _fullWrites);
                }

                Interlocked.Exchange(ref _lastWriteRows, next.RowCount);
                Interlocked.Add(ref _totalWrittenRows, next.RowCount);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedWrites);
                NetworkDiagnosticsLog.Write("server.checkpoint.error", $"{ex.GetType().Name}: {ex.Message}");
                lock (_sync)
                {
                    // An incremental checkpoint's rows are not captured again unless they change,
                    // so keep it for the next write; a newer full checkpoint replaces it.
                    _failed = _failed == null ? next : PersistenceCheckpoint.Merge(_failed, next);
                }
            }
            finally
            {
                RecordWriteDuration(Stopwatch.GetTimestamp() - started);
            }
        }
    }

    // A checkpoint updates rows that immediate writes queued before its capture insert, so the
    // write waits (bounded) until those have run (ImmediateWritesCommitted completes once they ran,
    // committed or failed). A closed write queue never completes the barrier; the write then goes
    // ahead after ImmediateWritesWaitTimeout (true). False only during Dispose, when the write
    // queue is in a lock episode or its shutdown drain deadline has passed: the barrier cannot
    // complete in time, and the caller defers the checkpoint to the final one.
    private bool WaitForImmediateWrites(PersistenceCheckpoint checkpoint)
    {
        if (checkpoint.ImmediateWritesCommitted is not { IsCompleted: false } barrier)
        {
            return true;
        }

        long deadline = Environment.TickCount64 + (long)ImmediateWritesWaitTimeout.TotalMilliseconds;
        try
        {
            while (true)
            {
                if (_disposing && _immediateWrites is { } queue &&
                    (queue.IsLocked || queue.RemainingDrainTime <= TimeSpan.Zero))
                {
                    return false;
                }

                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return true;
                }

                int slice = (int)(_immediateWrites == null ? remaining : Math.Min(remaining, BarrierPollMilliseconds));
                if (barrier.Wait(slice))
                {
                    return true;
                }
            }
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    private void RecordWriteDuration(long ticks)
    {
        Interlocked.Exchange(ref _lastWriteTicks, ticks);
        Interlocked.Add(ref _totalWriteTicks, ticks);
        Interlocked.Increment(ref _timedWrites);
        long max = Interlocked.Read(ref _maxWriteTicks);
        while (ticks > max)
        {
            long observed = Interlocked.CompareExchange(ref _maxWriteTicks, ticks, max);
            if (observed == max)
            {
                break;
            }

            max = observed;
        }
    }
}
