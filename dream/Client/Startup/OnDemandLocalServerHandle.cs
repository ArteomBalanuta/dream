using Dream.Server;

namespace Dream.Client.Startup;

// Defers starting the integrated server until local hosting is chosen. A client that only joins
// a remote server no longer pays for a second authoritative world, tick loop and open port.
public sealed class OnDemandLocalServerHandle : ILocalServerHandle
{
    private readonly Func<ILocalServerHandle> _start;
    private ILocalServerHandle? _started;

    public OnDemandLocalServerHandle(Func<ILocalServerHandle> start)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public bool IsStarted => _started != null;

    public bool IsReady => _started?.IsReady ?? false;

    public int BoundPort => _started?.BoundPort ?? 0;

    public string? ErrorMessage => _started?.ErrorMessage;

    public DedicatedServerPerformanceSnapshot LatestPerformance =>
        _started?.LatestPerformance ?? DedicatedServerPerformanceSnapshot.Empty;

    public void EnsureStarted()
    {
        _started ??= _start();
    }

    public ValueTask DisposeAsync()
    {
        return _started?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
