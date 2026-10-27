using Dream.Multiplayer.Diagnostics;
using Dream.Server.Persistence;

namespace Dream.Server;

// Runs checkpoint writes on one background worker so database I/O never blocks the
// authoritative tick. Writes are serialized; if a newer checkpoint arrives while one is
// still waiting, the older one is dropped because each checkpoint holds the full state.
public sealed class BackgroundCheckpointWriter : IDisposable
{
    private static readonly TimeSpan DisposeFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly IPersistenceCheckpointWriter _writer;
    private readonly object _sync = new();
    private PersistenceCheckpoint? _pending;
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private long _completedWrites;
    private long _failedWrites;
    private long _supersededCheckpoints;

    public BackgroundCheckpointWriter(IPersistenceCheckpointWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public long CompletedWrites => Interlocked.Read(ref _completedWrites);

    public long FailedWrites => Interlocked.Read(ref _failedWrites);

    public long SupersededCheckpoints => Interlocked.Read(ref _supersededCheckpoints);

    public void Enqueue(PersistenceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

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

    public void Dispose()
    {
        Flush(DisposeFlushTimeout);
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
        }
    }
}
