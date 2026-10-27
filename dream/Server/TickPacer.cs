using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dream.Server;

// Waits for the next authoritative tick deadline with ~1 ms precision. The previous loop slept
// with Task.Delay(1), which on Windows lasts a full 15.6 ms timer period; beating against the
// 15.625 ms tick produced loops with 0 or 2 ticks and irregular snapshot delivery.
// While active, raises the Windows timer resolution to 1 ms (as games commonly do) and sleeps
// coarsely, then spins briefly for the final sub-millisecond.
public sealed class TickPacer : IDisposable
{
    private const double SpinThresholdSeconds = 0.0015;
    private readonly bool _raisedTimerResolution;

    public TickPacer()
    {
        if (OperatingSystem.IsWindows())
        {
            _raisedTimerResolution = TimeBeginPeriod(1) == 0;
        }
    }

    // Seconds to wait before the next tick is due, given the clock's leftover accumulator.
    public static double SecondsUntilNextTick(double accumulatorSeconds, double tickSeconds)
    {
        return Math.Max(0d, tickSeconds - accumulatorSeconds);
    }

    public void WaitFor(double seconds, CancellationToken cancellationToken)
    {
        if (seconds <= 0d)
        {
            return;
        }

        long deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        while (!cancellationToken.IsCancellationRequested)
        {
            double remaining = (deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
            if (remaining <= 0d)
            {
                return;
            }

            if (remaining > SpinThresholdSeconds)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }

    public void Dispose()
    {
        if (_raisedTimerResolution && OperatingSystem.IsWindows())
        {
            TimeEndPeriod(1);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
