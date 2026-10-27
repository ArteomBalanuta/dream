using System.Diagnostics;
using Xunit;

namespace Dream.Tests.Utilities;

// Measures managed allocations on the calling thread in steady state.
//
// Tiered compilation makes a short measurement unreliable. A method is promoted from tier 0
// (through the instrumented PGO tier) to tier 1 after a call count that only starts once a
// time-based tiering delay has elapsed, and on-stack replacement can swap a hot loop mid-call.
// Those transitions charge a few hundred bytes to the calling thread whenever they land inside
// the measured window, so a single window reads 0 B in one run and 200 B in the next.
//
// The helper therefore warms the action well past the call-count threshold, then measures
// several rounds spread over time and returns the MINIMUM bytes per iteration. The allocation
// done by the code under test is the same in every round; runtime tiering noise only ever adds
// to a round, so the cleanest round is the steady-state figure.
public static class AllocationAssert
{
    // Above the runtime's tier-up call-count threshold (30) with margin for the PGO tier.
    private const int MinimumWarmupIterations = 64;
    private const int MinimumRounds = 5;

    // Longer than the runtime's tiering delay (100 ms), so rounds still run after the delayed
    // tier-up has had a chance to land and settle. Rounds repeat until both this span and
    // MinimumRounds are reached, so a fast action is measured over many rounds rather than over
    // a few milliseconds that all fall inside the delay.
    private static readonly TimeSpan MinimumMeasurementSpan = TimeSpan.FromMilliseconds(300);

    public static long MeasureBytesPerIteration(Action action, int iterations = 20, int warmupIterations = 3)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        int warmup = Math.Max(warmupIterations, MinimumWarmupIterations);
        for (int i = 0; i < warmup; i++)
        {
            action();
        }

        long best = long.MaxValue;
        Stopwatch elapsed = Stopwatch.StartNew();
        int rounds = 0;
        do
        {
            best = Math.Min(best, MeasureRound(action, iterations));
            rounds++;
        }
        while (rounds < MinimumRounds || elapsed.Elapsed < MinimumMeasurementSpan);

        return best;
    }

    public static long AtMost(long budgetBytesPerIteration, Action action, int iterations = 20, int warmupIterations = 3)
    {
        long bytesPerIteration = MeasureBytesPerIteration(action, iterations, warmupIterations);
        Assert.True(
            bytesPerIteration <= budgetBytesPerIteration,
            $"Allocated {bytesPerIteration} B per iteration; budget is {budgetBytesPerIteration} B.");
        return bytesPerIteration;
    }

    private static long MeasureRound(Action action, int iterations)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            action();
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }
}
