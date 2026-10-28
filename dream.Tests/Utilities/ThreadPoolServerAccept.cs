using Dream.Server;

namespace Dream.Tests.Utilities;

internal static class ThreadPoolServerAccept
{
    // DedicatedServerService.AcceptNextClientAsync starts the accepted connection's receive loop
    // from its own continuation, and none of the server's awaits use ConfigureAwait(false). Called
    // straight from an async test, that continuation captures xUnit's synchronization context, so
    // the server would read every client frame on the runner's few test threads, queued behind
    // whatever blocking tests hold them. In the game and the dedicated server there is no
    // synchronization context and the loop runs on the thread pool; starting the accept from the
    // pool gives the tests the same threading, so a client's commands reach the server before the
    // test ticks it however busy the parallel run is.
    public static Task<ServerConnection> AcceptOnThreadPoolAsync(this DedicatedServerService service)
    {
        return Task.Run(() => service.AcceptNextClientAsync().AsTask());
    }
}
