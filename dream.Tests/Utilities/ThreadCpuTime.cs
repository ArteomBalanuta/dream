using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dream.Tests.Utilities;

// CPU time the calling thread spent inside an action. A budget on synchronous, single-threaded work
// measured this way holds under a loaded test run: wall time also counts the time the thread waited
// for a core, which grows with whatever else runs in parallel, while CPU time grows only with the
// work (plus some cache and frequency noise). Windows only (GetThreadTimes); elsewhere it falls back
// to wall time, which is what the budgets were measured against.
internal static class ThreadCpuTime
{
    public static TimeSpan Measure(Action action, out TimeSpan wall)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!OperatingSystem.IsWindows() || !TryGetCurrentThreadCpu(out long before))
        {
            action();
            wall = stopwatch.Elapsed;
            return wall;
        }

        action();
        wall = stopwatch.Elapsed;
        if (!TryGetCurrentThreadCpu(out long after))
        {
            return wall;
        }

        return TimeSpan.FromTicks(after - before);
    }

    // Kernel plus user time, in 100 ns units (the same unit as TimeSpan ticks).
    private static bool TryGetCurrentThreadCpu(out long ticks)
    {
        if (GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user))
        {
            ticks = kernel + user;
            return true;
        }

        ticks = 0;
        return false;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
}
