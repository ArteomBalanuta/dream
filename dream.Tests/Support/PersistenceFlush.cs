using Xunit;
using Dream.Simulation.Persistence;

namespace Dream.Tests.Support;

// An awaitable PersistenceWriteQueue.Flush for tests.
//
// Flush waits on the worker with Task.Wait, so it parks the calling thread for as long as the
// worker takes, up to its timeout. In the parallel suite that thread is a test's own thread, and a
// test that parks it adds a blocked thread the pool then replaces, so the machine runs more
// runnable threads for the same cores while this test does nothing. A blocking flush added
// mid-test in the M8 admin family reproducibly timed the neighbouring loopback class's wait loops
// out: 3 of 3 runs red, 3 of 3 green once the flush was awaited instead.
//
// This has exactly Flush's guarantee and parks nothing. PersistenceWriteQueue.EnqueueRead runs its
// action on the worker after every write enqueued before the call has run (committed, or failed
// and counted in FailedWrites), so a read that only completes a TaskCompletionSource is a barrier:
// awaiting it returns at the moment Flush would have returned.
//
// The bound stays, and stays finite: an unbounded await would turn a queue that never drains from
// a failed test into a hung suite.
internal static class PersistenceFlush
{
    // The timeout every blocking Flush in these tests used.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Whether every write enqueued before this call had run within the timeout, like Flush's bool.
    public static async Task<bool> TryAsync(PersistenceWriteQueue? queue, TimeSpan? timeout = null)
    {
        if (queue == null)
        {
            return true;
        }

        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.EnqueueRead(() => barrier.TrySetResult()))
        {
            // Closed: nothing can be queued behind the barrier either, and a worker still draining
            // one can only be waited on the way a closed queue allows.
            return queue.Flush(timeout ?? DefaultTimeout);
        }

        try
        {
            await barrier.Task.WaitAsync(timeout ?? DefaultTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // The common form: the queue has to drain, and the test fails when it does not. Replaces
    // Assert.True(queue.Flush(30 s)) one for one.
    public static async Task AwaitAsync(PersistenceWriteQueue? queue, TimeSpan? timeout = null) =>
        Assert.True(await TryAsync(queue, timeout), "the persistence write queue did not drain");
}
