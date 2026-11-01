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
//
// Tiering state is per process, not per test, and the delay restarts whenever new tier 0 code
// runs. Under a parallel suite another test can have put a shared generic into the instrumented
// tier before this test starts, and it can stay there for longer than MinimumMeasurementSpan.
// Boxes in that tier are charged to every round, so all rounds read the same wrong figure. AtMost
// knows the budget, so it keeps measuring until a round meets it or MaximumSettleSpan runs out; a
// real allocation still fails, only later. MeasureBytesPerIteration has no budget to stop on and
// does not retry, so prefer AtMost for a zero-allocation gate.
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

    // How long AtMost waits for an over-budget reading to settle under a loaded test run.
    private static readonly TimeSpan MaximumSettleSpan = TimeSpan.FromSeconds(10);

    public static long MeasureBytesPerIteration(Action action, int iterations = 20, int warmupIterations = 3) =>
        Measure(action, iterations, warmupIterations, long.MinValue, MinimumMeasurementSpan);

    private static long Measure(Action action, int iterations, int warmupIterations, long budget, TimeSpan settleSpan)
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
        while (rounds < MinimumRounds
            || elapsed.Elapsed < MinimumMeasurementSpan
            || (best > budget && elapsed.Elapsed < settleSpan));

        return best;
    }

    public static long AtMost(long budgetBytesPerIteration, Action action, int iterations = 20, int warmupIterations = 3)
    {
        long bytesPerIteration = Measure(action, iterations, warmupIterations, budgetBytesPerIteration, MaximumSettleSpan);
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
