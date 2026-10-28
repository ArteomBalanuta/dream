using System.Diagnostics.CodeAnalysis;
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
    // True once the scene's session has ended; Game then leaves the play scene for the
    // connection screen, which shows the reason.
    public static bool HasEnded(ISessionEndSource scene, [NotNullWhen(true)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(scene);
        reason = scene.SessionEndReason;
        return reason != null;
    }

    // Releases what the ended session held, each exactly once and in this order:
    // 1. the play scene: its Dispose frees GL (Render, the model cache) first, so the caller must
    //    be on the render thread with the context current, then the runtime, then the network
    //    session;
    // 2. the local server handle: the integrated server, if one was started, after the scene's
    //    connection to it has closed. A handle that never started disposes nothing.
    // Every step runs even if an earlier one throws. The failures are returned rather than thrown:
    // the scene is gone either way, and the game carries on at the connection screen.
    public static IReadOnlyList<Exception> TearDown(IDisposable playScene, ILocalServerHandle localServer)
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

        try
        {
            localServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        return failures ?? (IReadOnlyList<Exception>)Array.Empty<Exception>();
    }
}
