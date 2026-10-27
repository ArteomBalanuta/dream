using Xunit;

namespace Dream.Tests.Utilities;

// Measures managed allocations on the calling thread. Warm-up runs absorb JIT, static
// initialization and first-use growth so the measured iterations show steady-state cost.
public static class AllocationAssert
{
    public static long MeasureBytesPerIteration(Action action, int iterations = 20, int warmupIterations = 3)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        for (int i = 0; i < warmupIterations; i++)
        {
            action();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            action();
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    public static long AtMost(long budgetBytesPerIteration, Action action, int iterations = 20, int warmupIterations = 3)
    {
        long bytesPerIteration = MeasureBytesPerIteration(action, iterations, warmupIterations);
        Assert.True(
            bytesPerIteration <= budgetBytesPerIteration,
            $"Allocated {bytesPerIteration} B per iteration; budget is {budgetBytesPerIteration} B.");
        return bytesPerIteration;
    }
}
