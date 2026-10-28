using System.Diagnostics;
using Dream.Multiplayer.Diagnostics;
using Dream.Server.Persistence;

namespace Dream.Server;

// Runs checkpoint writes on one background worker so database I/O never blocks the
// authoritative tick. Writes are serialized; if a newer checkpoint arrives while one is
// still waiting, the older one is dropped because each checkpoint holds the full state.
public sealed class BackgroundCheckpointWriter : IDisposable
{
    private readonly IPersistenceCheckpointWriter _writer;
    private readonly object _sync = new();
    private PersistenceCheckpoint? _pending;
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private long _completedWrites;
    private long _failedWrites;
    private long _supersededCheckpoints;
    private long _enqueuedCheckpoints;
    // Stopwatch ticks spent in IPersistenceCheckpointWriter.Write, successful or not.
    private long _lastWriteTicks;
    private long _maxWriteTicks;
    private long _totalWriteTicks;
    private long _timedWrites;

    public BackgroundCheckpointWriter(IPersistenceCheckpointWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public long CompletedWrites => Interlocked.Read(ref _completedWrites);

    public long FailedWrites => Interlocked.Read(ref _failedWrites);

    public long SupersededCheckpoints => Interlocked.Read(ref _supersededCheckpoints);

    // Every Enqueue call; the server compares it across a tick to see whether that tick captured one.
    public long EnqueuedCheckpoints => Interlocked.Read(ref _enqueuedCheckpoints);

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
            if (_pending != null)
            {
                _supersededCheckpoints++;
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
    // the host that captured it is disposed (see PersistenceWriteQueue.Dispose).
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

            long started = Stopwatch.GetTimestamp();
            try
            {
                _writer.Write(next);
                Interlocked.Increment(ref _completedWrites);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedWrites);
                NetworkDiagnosticsLog.Write("server.checkpoint.error", $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                RecordWriteDuration(Stopwatch.GetTimestamp() - started);
            }
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
