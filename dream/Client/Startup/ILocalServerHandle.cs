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

    // Whether the running server accepts dev commands (noclip, freeze). Single Player turns them on;
    // a hosted start keeps the dedicated server's environment-derived value, which is off unless
    // DREAM_DEV_COMMANDS=1. The client reads it so it never predicts a toggle the server will strip.
    // False until a server is ready.
    bool DevCommandsAllowed => false;

    // Where the next (or current) start listens.
    LocalServerListenOptions ListenOptions => LocalServerListenOptions.Loopback;

    // Sets where the next start listens; a running server on other options is stopped first. False
    // (nothing changed) while a start is pending or after dispose. Fixed handles accept only their own options.
    bool TryConfigureListen(LocalServerListenOptions options) => options == ListenOptions;
}
