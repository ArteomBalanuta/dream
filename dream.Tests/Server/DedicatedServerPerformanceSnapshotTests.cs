using Xunit;
using Dream.Server;

namespace Dream.Tests.Server;

public class DedicatedServerPerformanceSnapshotTests
{
    [Fact]
    public void Empty_Reports_No_Server_Timings()
    {
        Assert.False(DedicatedServerPerformanceSnapshot.Empty.HasCounters);
    }

    [Fact]
    public void ToHudLines_Formats_Server_Timings()
    {
        DedicatedServerPerformanceSnapshot snapshot = new(
            TickMilliseconds: 1.5,
            SnapshotMilliseconds: 2.5,
            BroadcastMilliseconds: 3.5,
            Sampled: true);

        string[] lines = snapshot.ToHudLines().ToArray();

        Assert.Contains("Server: tick 1.50 ms, snapshot 2.50 ms, broadcast 3.50 ms", lines);
    }
}
