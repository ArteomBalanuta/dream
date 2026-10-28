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
// change how any code schedules work, and the production game and server never depend on the pool
// size (their tick, render and transport loops run on dedicated threads). The current values are
// kept when they are already higher, for example when DOTNET_ThreadPool_MinThreads is set.
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
