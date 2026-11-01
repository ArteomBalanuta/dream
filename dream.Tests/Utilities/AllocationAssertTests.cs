using System.Diagnostics;
using Xunit;
using Xunit.Sdk;

namespace Dream.Tests.Utilities;

public class AllocationAssertTests
{
    private static object? _sink;

    // The shape of a shared generic stuck in the instrumented tier: every round over the helper's
    // 300 ms minimum allocates, then the allocation stops.
    [Fact]
    public void AtMost_WaitsOutAnAllocationThatOutlastsTheMinimumSpan()
    {
        Stopwatch since = Stopwatch.StartNew();

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            if (since.ElapsedMilliseconds < 1000)
            {
                _sink = new object();
            }
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void AtMost_StillFailsAnAllocationThatNeverStops()
    {
        Assert.ThrowsAny<XunitException>(() => AllocationAssert.AtMost(0, () => _sink = new object()));
    }
}
