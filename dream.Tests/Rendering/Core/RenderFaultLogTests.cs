using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.Core;

public class RenderFaultLogTests
{
    [Fact]
    public void Report_LogsFirstFaultInFullThenOnlyCountsAtPowersOfTwo()
    {
        List<string> lines = [];
        RenderFaultLog log = new(lines.Add);

        for (int index = 0; index < 10; index++)
        {
            log.Report("chunks", new InvalidOperationException($"failure {index}"));
        }

        Assert.Equal(4, lines.Count);
        Assert.Contains("InvalidOperationException", lines[0]);
        Assert.Contains("failure 0", lines[0]);
        Assert.Contains("2 faults", lines[1]);
        Assert.Contains("4 faults", lines[2]);
        Assert.Contains("8 faults", lines[3]);
        Assert.Contains("failure 7", lines[3]);
    }

    [Fact]
    public void Report_RateLimitsEachSourceIndependently()
    {
        List<string> lines = [];
        RenderFaultLog log = new(lines.Add);
        InvalidOperationException last = new("b2");

        log.Report("a", new InvalidOperationException("a1"));
        log.Report("b", new InvalidOperationException("b1"));
        log.Report("a", new InvalidOperationException("a2"));
        log.Report("a", new InvalidOperationException("a3"));
        log.Report("b", last);

        Assert.Equal(4, lines.Count);
        Assert.Equal(5, log.FaultCount);
        Assert.Equal(3, log.GetFaultCount("a"));
        Assert.Equal(2, log.GetFaultCount("b"));
        Assert.Equal(0, log.GetFaultCount("missing"));
        Assert.Same(last, log.LastFault);
    }

    [Fact]
    public void Report_IsThreadSafe()
    {
        int lineCount = 0;
        RenderFaultLog log = new(_ => Interlocked.Increment(ref lineCount));

        Parallel.For(0, 1000, index => log.Report("parallel", new InvalidOperationException(index.ToString())));

        Assert.Equal(1000, log.FaultCount);
        Assert.Equal(1000, log.GetFaultCount("parallel"));
        // 1 full line plus counts at 2, 4, ..., 512.
        Assert.Equal(10, lineCount);
    }

    [Fact]
    public void Report_SwallowsWriterFailures()
    {
        RenderFaultLog log = new(_ => throw new IOException("disk full"));

        log.Report("source", new InvalidOperationException("boom"));

        Assert.Equal(1, log.FaultCount);
    }

    [Fact]
    public void Shared_IsSingleProcessWideInstance()
    {
        Assert.Same(RenderFaultLog.Shared, RenderFaultLog.Shared);
    }
}
