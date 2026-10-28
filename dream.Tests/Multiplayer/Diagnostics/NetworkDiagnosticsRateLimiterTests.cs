using System.Diagnostics;
using Xunit;
using Dream.Multiplayer.Diagnostics;

namespace Dream.Tests.Multiplayer.Diagnostics;

// SCALE-7: the limiter is process-wide and its keys include per-connection ones
// ("server.rx.command.<connection>"), so it used to grow by two keys per connection forever.
public sealed class NetworkDiagnosticsRateLimiterTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly long IntervalTicks = Stopwatch.Frequency;

    [Fact]
    public void ShouldLog_LetsOneLinePerIntervalThrough()
    {
        NetworkDiagnosticsRateLimiter limiter = new();
        long now = 1_000 * IntervalTicks;

        Assert.True(limiter.ShouldLog("k", Interval, now));
        Assert.False(limiter.ShouldLog("k", Interval, now + (IntervalTicks / 2)));
        Assert.True(limiter.ShouldLog("k", Interval, now + IntervalTicks));
        Assert.True(limiter.ShouldLog("other", Interval, now + IntervalTicks));
    }

    [Fact]
    public void KeysOfClosedConnections_ExpireOnceTheirIntervalHasPassed()
    {
        NetworkDiagnosticsRateLimiter limiter = new();
        long now = 1_000 * IntervalTicks;
        for (int i = 0; i < 50; i++)
        {
            Assert.True(limiter.ShouldLog($"server.rx.command.conn-{i}", Interval, now));
        }

        Assert.Equal(50, limiter.Count);

        // Still inside the interval: nothing may be forgotten, or the next call would log again.
        limiter.SweepExpired(now + IntervalTicks - 1);
        Assert.Equal(50, limiter.Count);
        Assert.False(limiter.ShouldLog("server.rx.command.conn-0", Interval, now + IntervalTicks - 1));

        limiter.SweepExpired(now + IntervalTicks);
        Assert.Equal(0, limiter.Count);
    }

    [Fact]
    public void NewKeys_SweepExpiredOnes_SoUnboundedConnectionsKeepTheTableSmall()
    {
        NetworkDiagnosticsRateLimiter limiter = new();
        long now = 1_000 * IntervalTicks;
        int maxCount = 0;

        // 100k connections, ten per second, two keys each, every key used once.
        for (int i = 0; i < 100_000; i++)
        {
            long at = now + (i * IntervalTicks / 10);
            Assert.True(limiter.ShouldLog($"server.rx.command.conn-{i}", Interval, at));
            Assert.True(limiter.ShouldLog($"server.tx.snapshot.conn-{i}", Interval, at));
            maxCount = Math.Max(maxCount, limiter.Count);
        }

        Assert.True(maxCount <= 256, $"the limiter held up to {maxCount} keys");
    }

    [Fact]
    public void ManyLiveKeys_AreCappedAtMaxEntries()
    {
        NetworkDiagnosticsRateLimiter limiter = new();
        long now = 1_000 * IntervalTicks;
        for (int i = 0; i < NetworkDiagnosticsRateLimiter.MaxEntries * 3; i++)
        {
            Assert.True(limiter.ShouldLog($"k{i}", TimeSpan.FromMinutes(10), now + i));
            Assert.True(limiter.Count <= NetworkDiagnosticsRateLimiter.MaxEntries);
        }

        // The newest keys survive the evictions.
        Assert.False(limiter.ShouldLog($"k{(NetworkDiagnosticsRateLimiter.MaxEntries * 3) - 1}", TimeSpan.FromMinutes(10), now + (NetworkDiagnosticsRateLimiter.MaxEntries * 3)));
    }

    [Fact]
    public void AKeyWithALongerIntervalIsNotForgottenEarly()
    {
        NetworkDiagnosticsRateLimiter limiter = new();
        long now = 1_000 * IntervalTicks;
        Assert.True(limiter.ShouldLog("slow", TimeSpan.FromSeconds(5), now));
        Assert.True(limiter.ShouldLog("fast", Interval, now));

        limiter.SweepExpired(now + (2 * IntervalTicks));
        Assert.False(limiter.ShouldLog("slow", TimeSpan.FromSeconds(5), now + (2 * IntervalTicks)));
        Assert.True(limiter.ShouldLog("slow", TimeSpan.FromSeconds(5), now + (5 * IntervalTicks)));
    }

    [Fact]
    public void ShouldLog_OnAnExistingKey_DoesNotAllocate()
    {
        NetworkDiagnosticsRateLimiter limiter = new();
        const string key = "server.tx.snapshot.conn";
        long now = 1_000 * IntervalTicks;
        limiter.ShouldLog(key, Interval, now);
        limiter.ShouldLog(key, Interval, now + 1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool suppressed = limiter.ShouldLog(key, Interval, now + 2);
        bool passed = limiter.ShouldLog(key, Interval, now + IntervalTicks);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(suppressed);
        Assert.True(passed);
        Assert.Equal(0, allocated);
    }
}
