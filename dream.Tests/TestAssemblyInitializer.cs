using System.Runtime.CompilerServices;

namespace Dream.Tests;

// Runs once when the test assembly loads, before xUnit starts any test.
//
// The suite runs many test classes at once, and several of them block a thread-pool thread for a
// long time: sync-over-async in loopback tests, decode gates, read loops, stress requesters. The
// pool starts with one worker per core and then injects new threads slowly (about one or two a
// second). A short continuation or Task.Run that a test waits on can then sit in the queue for
// seconds behind blocked work, so tests flake on their deadlines or take many times their real
// cost (for example AsyncResourceLoaderTests.PumpUploads_Respects_MaxPerFrame took 6.9 s and failed).
//
// Raising the pool's MINIMUM only lets it create threads without the injection delay; it does not
// change how any code schedules work. Production does use the thread pool for transport: the
// ServerConnection writer loop and the RemoteClientSession send and receive loops are Task.Run
// tasks. It stays unaffected because production has a handful of concurrent connections and
// never blocks a pool thread in a test-style wait (no sync-over-async), so its pool is never
// starved and the injection delay never shows; a bug that depended on starvation would need that
// blocking, which only the tests add. The current values are kept when they are already higher,
// for example when DOTNET_ThreadPool_MinThreads is set.
internal static class TestAssemblyInitializer
{
    internal const int ThreadsPerCore = 4;

    [ModuleInitializer]
    internal static void RaiseThreadPoolMinimum()
    {
        int target = Environment.ProcessorCount * ThreadsPerCore;
        ThreadPool.GetMinThreads(out int worker, out int io);
        ThreadPool.SetMinThreads(Math.Max(worker, target), Math.Max(io, target));
    }
}
