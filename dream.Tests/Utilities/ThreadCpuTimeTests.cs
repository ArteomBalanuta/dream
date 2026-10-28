using System.Diagnostics;
using Xunit;

namespace Dream.Tests.Utilities;

public class ThreadCpuTimeTests
{
    // Waiting costs no CPU, so a budget measured this way does not grow with time spent off-core.
    [Fact]
    public void Sleeping_Costs_Almost_No_Cpu_Time()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Wall time is the fallback there, so a sleep is counted in full.
            return;
        }

        TimeSpan cpu = ThreadCpuTime.Measure(() => Thread.Sleep(300), out TimeSpan wall);

        Assert.True(wall >= TimeSpan.FromMilliseconds(250), $"wall {wall}");
        Assert.True(cpu < TimeSpan.FromMilliseconds(100), $"cpu {cpu}");
    }

    [Fact]
    public void Spinning_Is_Counted()
    {
        TimeSpan cpu = ThreadCpuTime.Measure(
            () =>
            {
                Stopwatch spin = Stopwatch.StartNew();
                while (spin.Elapsed < TimeSpan.FromMilliseconds(200))
                {
                }
            },
            out TimeSpan wall);

        Assert.True(wall >= TimeSpan.FromMilliseconds(200), $"wall {wall}");
        // At least part of the spin ran on a core, and never more than the wall time it took
        // (plus the 15.6 ms accounting granularity).
        Assert.True(cpu > TimeSpan.Zero, $"cpu {cpu}");
        Assert.True(cpu <= wall + TimeSpan.FromMilliseconds(32), $"cpu {cpu}, wall {wall}");
    }
}
