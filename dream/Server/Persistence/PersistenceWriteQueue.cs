using Dream.Multiplayer.Diagnostics;

namespace Dream.Server.Persistence;

// Ordered queue for the runtime's immediate persistence writes (placements, fractures, enemy
// spawn/death, profile and inventory saves). In background mode one worker runs them in
// enqueue order, so SQLite connection setup and fsync no longer happen on the simulation tick.
// Inline mode runs each write immediately; tests and tools that read the repository right
// after an action use it.
public sealed class PersistenceWriteQueue : IDisposable
{
    private readonly bool _background;
    private readonly object _sync = new();
    private readonly Queue<Action> _pending = new();
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private long _completedWrites;
    private long _failedWrites;
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

    // Writes queued but not yet started (always 0 in inline mode). A counter kept beside the queue,
    // so the server tick reads it without taking the writer's lock.
    public int PendingCount => Volatile.Read(ref _pendingCount);

    public void Enqueue(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (!_background)
        {
            Execute(write);
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
    // write per piece) could land rows in the database long after the server stopped.
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
            Action next;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    _running = false;
                    return;
                }

                next = _pending.Dequeue();
                Interlocked.Decrement(ref _pendingCount);
            }

            Execute(next);
        }
    }

    private void Execute(Action write)
    {
        try
        {
            write();
            Interlocked.Increment(ref _completedWrites);
        }
        catch (Exception ex) when (_background)
        {
            Interlocked.Increment(ref _failedWrites);
            NetworkDiagnosticsLog.Write("server.persistence.error", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
