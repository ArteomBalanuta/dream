using Dream.Server;

namespace Dream.Client.Startup;

public interface ILocalServerHandle : IAsyncDisposable
{
    bool IsReady { get; }

    int BoundPort { get; }

    string? ErrorMessage { get; }

    DedicatedServerPerformanceSnapshot LatestPerformance { get; }
}
