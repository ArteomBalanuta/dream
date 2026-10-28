using System.Diagnostics.CodeAnalysis;
using Dream.Client.Networking;
using Dream.Client.Startup;

namespace Dream.Application;

// A scene whose network session can end on its own: a lost connection, a silent server, a failed
// or timed-out join. PlayScene implements it.
public interface ISessionEndSource
{
    // Why the session ended, or null while it is live.
    string? SessionEndReason { get; }
}

// The play-to-startup switch Game makes when the play session ends (NETR-2). It lives outside Game,
// which needs a window and a GL context, so the decision and the teardown order can be tested.
public static class PlaySessionEndTransition
{
    // How long TearDown (and Game's unload) waits for the integrated server to stop before
    // abandoning it.
    public static readonly TimeSpan LocalServerStopTimeout = TimeSpan.FromSeconds(5);

    // True once the scene's session has ended; Game then leaves the play scene for the
    // connection screen, which shows the reason.
    public static bool HasEnded(ISessionEndSource scene, [NotNullWhen(true)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(scene);
        reason = scene.SessionEndReason;
        return reason != null;
    }

    // The connection screen's endpoint after a session ends, and whether a connect to it starts
    // a new integrated server. Only a session to this game's own integrated server (one that was
    // started, now stopped with the session) is redirected, and only lazily: the endpoint stays
    // as it was, nothing starts here, and LocalServerRelaunchConnector starts the new server when
    // the player connects to it. Any other endpoint, a loopback dedicated server included, is
    // kept exactly as it was.
    public static ClientConnectionOptions ReturnOptions(RemoteClientOptions endedOptions)
    {
        ArgumentNullException.ThrowIfNull(endedOptions);
        return new ClientConnectionOptions(endedOptions.Host, endedOptions.Port.ToString(), endedOptions.DisplayName);
    }

    // Releases what the ended session held, each exactly once and in this order:
    // 1. the play scene: its Dispose frees GL (Render, the model cache) first, so the caller must
    //    be on the render thread with the context current, then the runtime, then the network
    //    session;
    // 2. the local server handle: the integrated server, if one was started, after the scene's
    //    connection to it has closed. A handle that never started disposes nothing. It stops on a
    //    background task and is waited for at most localServerStopTimeout (LocalServerStopTimeout
    //    by default); see StopLocalServer.
    // Every step runs even if an earlier one throws. The failures are returned rather than thrown:
    // the scene is gone either way, and the game carries on at the connection screen.
    public static IReadOnlyList<Exception> TearDown(
        IDisposable playScene,
        ILocalServerHandle localServer,
        TimeSpan? localServerStopTimeout = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(playScene);
        ArgumentNullException.ThrowIfNull(localServer);

        List<Exception>? failures = null;
        try
        {
            playScene.Dispose();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        if (StopLocalServer(localServer, localServerStopTimeout, log) is Exception stopFailure)
        {
            (failures ??= []).Add(stopFailure);
        }

        return failures ?? (IReadOnlyList<Exception>)Array.Empty<Exception>();
    }

    // Disposes the local server handle on a background task and waits for it at most timeout
    // (LocalServerStopTimeout by default), so a hung integrated server (a stuck tick, a connection
    // loop that never returns) cannot freeze the calling update or render thread. A handle that
    // does not finish in time is abandoned with a log line: its loops run on background threads,
    // so the process exit ends them, and a fault it raises later is observed and logged. The next
    // integrated server binds a fresh ephemeral port (IntegratedLocalServerHandle.StartAsync with
    // port 0), so it never collides with an abandoned one. Returns the disposal's failure, or null
    // when it completed or was abandoned.
    public static Exception? StopLocalServer(
        ILocalServerHandle localServer,
        TimeSpan? timeout = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(localServer);
        TimeSpan wait = timeout ?? LocalServerStopTimeout;
        log ??= Console.WriteLine;

        Task stop = Task.Run(async () => await localServer.DisposeAsync());
        bool finished;
        try
        {
            finished = stop.Wait(wait);
        }
        catch (AggregateException)
        {
            finished = true;
        }

        if (!finished)
        {
            log($"[session] local server did not stop within {wait.TotalSeconds:0.#} s; abandoning it");
            _ = stop.ContinueWith(
                static (task, state) => ((Action<string>)state!)(
                    $"[session] abandoned local server failed to stop: {task.Exception!.GetBaseException().Message}"),
                log,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return null;
        }

        try
        {
            stop.GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
