using Dream.Server;

namespace Dream.Client.Startup;

public interface ILocalServerHandle : IAsyncDisposable
{
    bool IsReady { get; }

    int BoundPort { get; }

    string? ErrorMessage { get; }

    DedicatedServerPerformanceSnapshot LatestPerformance { get; }

    // Handles that start lazily report false until EnsureStarted runs.
    bool IsStarted => true;

    void EnsureStarted()
    {
    }

    // Completes once a start that EnsureStarted could not finish at once (it waits for the
    // previous integrated server to finish saving) has run; IsReady, BoundPort and ErrorMessage
    // then describe the started server. Handles that start synchronously are always done.
    Task WaitForStartAsync() => Task.CompletedTask;

    // What the connection screen shows while a requested start is still waiting, or null.
    string? StartingStatus => null;

    // Forgets a failed start (ErrorMessage set) so EnsureStarted can try again, for example after
    // the window that held the world was closed. A running or still-starting server is unaffected.
    void ResetFailure()
    {
    }
}
