using Dream.Server;

namespace Dream.Client.Startup;

// Defers starting the integrated server until local hosting is chosen. A client that only joins
// a remote server no longer pays for a second authoritative world, tick loop and open port.
//
// startAfter (optional) returns the stop of every earlier integrated server that may still be
// running. They share one world database (ServerBootstrap.CreatePersistenceBundle), so a new
// server must not open it while an older one can still write to it. When that stop has not
// finished, EnsureStarted does not block its caller (the update thread): the start is queued
// behind the stop, IsStarted turns true at once, IsReady once the server runs, and
// WaitForStartAsync completes when the queued start has run.
public sealed class OnDemandLocalServerHandle : ILocalServerHandle
{
    public const string WaitingForPreviousServerStatus =
        "Previous local server is still saving; the local server starts when it finishes...";

    private readonly Func<LocalServerListenOptions, ILocalServerHandle> _start;
    private readonly Func<Task>? _startAfter;
    private volatile ILocalServerHandle? _started;
    private volatile string? _startFailure;
    private volatile bool _disposed;
    private volatile LocalServerListenOptions _listen = LocalServerListenOptions.Loopback;
    private Task? _pendingStart;
    // The stop of servers detached by TryConfigureListen.
    private Task _ownStop = Task.CompletedTask;

    // The start receives the listen options configured last (Loopback until TryConfigureListen).
    public OnDemandLocalServerHandle(Func<LocalServerListenOptions, ILocalServerHandle> start, Func<Task>? startAfter = null)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _startAfter = startAfter;
    }

    public OnDemandLocalServerHandle(Func<ILocalServerHandle> start, Func<Task>? startAfter = null)
        : this(start is null ? null! : _ => start(), startAfter)
    {
    }

    public LocalServerListenOptions ListenOptions => _listen;

    // Same options: true, any running server is kept. Refused after dispose or while a start is
    // pending. Otherwise a server that exists (running or failed) is detached and its stop begins
    // without blocking the caller; the next EnsureStarted queues behind that stop, so the old
    // server's port and world are released before a new one starts.
    public bool TryConfigureListen(LocalServerListenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_disposed)
        {
            return false;
        }

        if (options == _listen)
        {
            return true;
        }

        if (_pendingStart is { IsCompleted: false })
        {
            return false;
        }

        ILocalServerHandle? previous = _started;
        if (previous != null)
        {
            _started = null;
            if (_listen.AllowRemotePlayers)
            {
                Console.Error.WriteLine($"[host] stopped hosting on port {previous.BoundPort}");
            }

            _ownStop = ChainStop(_ownStop, previous);
        }

        _startFailure = null;
        _pendingStart = null;
        _listen = options;
        return true;
    }

    private static async Task ChainStop(Task earlier, ILocalServerHandle server)
    {
        // Off the caller's (update) thread: a stop whose run loop already ended disposes the host,
        // persistence drain included, inline in DisposeAsync.
        await Task.Yield();
        await earlier.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        try
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[startup] local server stop failed: {ex.Message}");
        }
    }

    public bool IsStarted => _started != null || _pendingStart != null;

    public bool IsReady => _started?.IsReady ?? false;

    public int BoundPort => _started?.BoundPort ?? 0;

    public string? ErrorMessage => _started?.ErrorMessage ?? _startFailure;

    public DedicatedServerPerformanceSnapshot LatestPerformance =>
        _started?.LatestPerformance ?? DedicatedServerPerformanceSnapshot.Empty;

    // True while a requested start waits for the previous server to finish saving.
    public bool IsWaitingForPreviousServer => _started == null && _pendingStart is { IsCompleted: false };

    public string? StartingStatus => IsWaitingForPreviousServer ? WaitingForPreviousServerStatus : null;

    public void EnsureStarted()
    {
        if (_started != null || _pendingStart != null || _disposed)
        {
            return;
        }

        Task previousStop = _ownStop.IsCompleted
            ? _startAfter?.Invoke() ?? Task.CompletedTask
            : Task.WhenAll(_startAfter?.Invoke() ?? Task.CompletedTask, _ownStop);
        if (previousStop.IsCompleted)
        {
            _started = _start(_listen);
            return;
        }

        _pendingStart = StartAfterAsync(previousStop);
    }

    // Clears a failed start so the next EnsureStarted runs the start again. The failed handle holds
    // nothing to stop (a failed start released its host), so it is disposed on the spot.
    public void ResetFailure()
    {
        if (_disposed || _pendingStart is { IsCompleted: false })
        {
            return;
        }

        ILocalServerHandle? failed = _started;
        if (failed != null)
        {
            if (failed.ErrorMessage == null)
            {
                return;
            }

            _started = null;
            failed.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        else if (_startFailure == null)
        {
            return;
        }

        _startFailure = null;
        _pendingStart = null;
    }

    public Task WaitForStartAsync()
    {
        return _pendingStart ?? Task.CompletedTask;
    }

    // Never faults: a failure of the previous stop was already reported by whoever stopped it,
    // and a failed start shows as ErrorMessage.
    private async Task StartAfterAsync(Task previousStop)
    {
        await previousStop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_disposed)
        {
            return;
        }

        try
        {
            _started = _start(_listen);
        }
        catch (Exception ex)
        {
            _startFailure = ex.Message;
        }
    }

    // A start still waiting is cancelled if it has not begun, or waited for (and its server
    // stopped) if it has: _pendingStart completes only after _started is set.
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_pendingStart != null)
        {
            await _pendingStart.ConfigureAwait(false);
        }

        await _ownStop.ConfigureAwait(false);
        if (_started != null)
        {
            await _started.DisposeAsync();
        }
    }
}
