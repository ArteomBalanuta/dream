using System.Runtime.InteropServices;

namespace Dream.Server;

// Every orderly stop signal the host OS can send a dedicated server, routed into one cancellation
// and one bounded wait, so each of them reaches the same drain path Ctrl+C already reached:
// host.Dispose() -> presence release -> checkpoint-writer drain -> final full checkpoint ->
// PersistenceWriteQueue.Dispose.
//
// Ctrl+C and Ctrl+Break arrive as Console.CancelKeyPress; SIGTERM (docker stop, systemctl stop,
// kill), SIGINT, SIGQUIT and SIGHUP (a closed terminal) arrive as PosixSignalRegistration; a closed
// console window, a logoff or a system shutdown arrives as one of those or as ProcessExit. Only the
// first of them starts the shutdown -- the rest see the flag and do nothing -- so the teardown runs
// exactly once however many signals land, and a signal arriving while the drain is already running
// only waits for it.
//
// Nothing here blocks indefinitely: the OS kills the process after its own grace period (Windows
// allows a closed console window about 5 s; a SIGTERM supervisor usually 10 s), so a handler waits
// at most DrainGrace for the drain and then returns and lets the process go, drained or not.
public sealed class OrderlyShutdownSignals : IDisposable
{
    // The longest a signal handler blocks waiting for the drain. Comfortably inside the shortest
    // grace period any of these signals carries (Windows' ~5 s console-close allowance).
    public static readonly TimeSpan DefaultDrainGrace = TimeSpan.FromSeconds(4);

    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _drained = new(false);
    private readonly List<IDisposable> _registrations = [];
    private readonly Action<string> _log;
    private readonly TimeSpan _drainGrace;
    private ConsoleCancelEventHandler? _cancelHandler;
    private EventHandler? _processExitHandler;
    private int _requested;
    private int _disposed;

    public OrderlyShutdownSignals(Action<string>? log = null, TimeSpan? drainGrace = null)
    {
        _log = log ?? Console.Error.WriteLine;
        _drainGrace = drainGrace ?? DefaultDrainGrace;
    }

    // Cancelled by the first stop signal; the server's RunAsync runs on it.
    public CancellationToken Token => _cancellation.Token;

    // True once a signal has asked for the shutdown. Later signals leave it alone.
    public bool ShutdownRequested => Volatile.Read(ref _requested) != 0;

    // How many stop signals arrived, including the ones that found the shutdown already running.
    public int SignalCount { get; private set; }

    // The signal that started the shutdown, or null while none has.
    public string? FirstSignal { get; private set; }

    // Installs the real OS hooks. Called once, by the dedicated server; tests drive Request
    // directly instead. A signal this platform does not have is simply not registered.
    public void Install()
    {
        _cancelHandler = (_, args) =>
        {
            // Keep the process alive and let the drain below run instead of the runtime's abort.
            args.Cancel = true;
            Request("Ctrl+C");
        };
        Console.CancelKeyPress += _cancelHandler;

        foreach (PosixSignal signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGHUP })
        {
            PosixSignal captured = signal;
            try
            {
                _registrations.Add(PosixSignalRegistration.Create(captured, context =>
                {
                    // Suppresses the runtime's own terminate-now default, so the drain gets its turn.
                    context.Cancel = true;
                    RequestAndWaitForDrain(captured.ToString());
                }));
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or ArgumentOutOfRangeException)
            {
                // Not a signal this OS raises; the other hooks still cover its stop paths.
            }
        }

        // The last resort for a stop that reaches neither of the above (a closed console window or a
        // logoff on some Windows builds). On a normal exit the drain has already finished by the time
        // this runs, so the wait returns at once.
        _processExitHandler = (_, _) => RequestAndWaitForDrain("ProcessExit");
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
    }

    // Starts the shutdown if no signal has yet, and does nothing at all if one has: the drain that
    // the first signal set going is the only one, so no second teardown is ever started.
    public void Request(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        SignalCount++;
        if (Interlocked.Exchange(ref _requested, 1) != 0)
        {
            _log($"[server] {source} while already shutting down: the stop already under way finishes the save");
            return;
        }

        FirstSignal = source;
        _log($"[server] {source}: stopping and saving the world");
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The server already finished; nothing left to cancel.
        }
    }

    // Request, then block the signal handler for at most the drain grace so the queued writes and
    // the final checkpoint land before the OS takes the process. Returns whether the drain finished.
    public bool RequestAndWaitForDrain(string source)
    {
        Request(source);
        return WaitForDrain();
    }

    // Waits at most the drain grace for NotifyDrained. Never waits longer, drained or not.
    public bool WaitForDrain()
    {
        if (_drained.IsSet)
        {
            return true;
        }

        bool drained;
        try
        {
            drained = _drained.Wait(_drainGrace);
        }
        catch (ObjectDisposedException)
        {
            return true;
        }

        if (!drained)
        {
            _log($"[server] still saving after {_drainGrace.TotalSeconds:0} s; the process is being stopped without the rest");
        }

        return drained;
    }

    // Called once the server's teardown has run to completion -- after the host, the service and the
    // transport have been disposed -- which releases every signal handler waiting on the drain.
    public void NotifyDrained()
    {
        try
        {
            _drained.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Released before the handles go, so a signal landing now finds nothing half-disposed.
        if (_cancelHandler != null)
        {
            Console.CancelKeyPress -= _cancelHandler;
            _cancelHandler = null;
        }

        if (_processExitHandler != null)
        {
            AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
            _processExitHandler = null;
        }

        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }

        _registrations.Clear();
        _drained.Set();
        _drained.Dispose();
        _cancellation.Dispose();
    }
}
