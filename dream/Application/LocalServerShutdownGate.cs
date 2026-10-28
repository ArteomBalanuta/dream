namespace Dream.Application;

// Owns the stops of every integrated server Game has let go of. Each integrated server opens the
// same world database (ServerBootstrap.CreatePersistenceBundle, player-profiles.db in WAL mode),
// and a stop can outlast PlaySessionEndTransition.LocalServerStopTimeout: the persistence drain
// legitimately takes up to its deadline plus busy_timeout. A stop the session-end teardown stops
// waiting for is tracked here instead of being forgotten, so the next integrated server starts
// only after it (OnDemandLocalServerHandle's startAfter) and the window close waits for it.
public sealed class LocalServerShutdownGate
{
    private readonly object _gate = new();
    private Task _pending = Task.CompletedTask;

    // Completes once every tracked stop has finished. Never faults: each stop's failure is
    // reported where it was started.
    public Task Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    public bool IsPending => !Pending.IsCompleted;

    public void Track(Task stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        lock (_gate)
        {
            Task previous = _pending;
            _pending = Task.WhenAll(previous, stop).ContinueWith(
                static _ => { },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
