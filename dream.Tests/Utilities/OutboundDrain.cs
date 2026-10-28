using System.Diagnostics;
using Dream.Server;

namespace Dream.Tests.Utilities;

// Flushes a server connection the way a test waits on it after every tick, without paying a
// timer tick for it.
//
// ServerConnection.FlushOutboundAsync re-checks with Task.Delay(1). On Windows that sleeps a
// whole timer period (about 15.6 ms) whenever the writer is still busy at the first check, which
// it nearly always is right after a tick enqueued a snapshot. Loopback tests that flush every
// tick spent most of their wall time in those sleeps. This helper first waits for the progress
// the flush is about to check (the queue empty and the last send returned), spinning briefly
// instead of sleeping, and then calls FlushOutboundAsync, which still decides the result and
// still enforces the caller's deadline. A writer that is truly slow or stalled only costs the
// short spin before the ordinary flush takes over.
internal static class OutboundDrain
{
    // Long enough for a loopback send of a snapshot, short enough not to matter if the writer is
    // genuinely stalled (the flush below then waits with its own deadline).
    private static readonly long SpinTicks = Stopwatch.Frequency / 50;

    // The writer clears its busy flag right after the dequeued message's send returns; this many
    // spins covers that window once the queue is empty.
    private const int SettleSpins = 40;

    public static ValueTask<bool> FlushAsync(ServerConnection connection, TimeSpan timeout)
    {
        WaitForEmptyQueue(connection);
        // On the thread pool, so the flush's re-check continuations do not queue on xUnit's
        // synchronization context behind blocking tests (see ThreadPoolServerAccept).
        return new ValueTask<bool>(Task.Run(() => connection.FlushOutboundAsync(timeout).AsTask()));
    }

    public static async ValueTask<bool> FlushAllAsync(IEnumerable<ServerConnection> connections, TimeSpan timeout)
    {
        bool flushed = true;
        foreach (ServerConnection connection in connections)
        {
            flushed &= await FlushAsync(connection, timeout);
        }

        return flushed;
    }

    private static void WaitForEmptyQueue(ServerConnection connection)
    {
        long deadline = Stopwatch.GetTimestamp() + SpinTicks;
        SpinWait spinner = default;
        while (connection.PendingOutboundCount > 0 && !connection.IsFaulted && Stopwatch.GetTimestamp() < deadline)
        {
            spinner.SpinOnce(sleep1Threshold: -1);
        }

        for (int i = 0; i < SettleSpins; i++)
        {
            Thread.SpinWait(32);
        }
    }
}
