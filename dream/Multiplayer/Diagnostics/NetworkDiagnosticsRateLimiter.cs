using System.Collections.Concurrent;
using System.Diagnostics;

namespace Dream.Multiplayer.Diagnostics;

public sealed class NetworkDiagnosticsRateLimiter
{
    private readonly ConcurrentDictionary<string, long> _lastLoggedTicks = new(StringComparer.Ordinal);

    public bool ShouldLog(string key, TimeSpan interval)
    {
        return ShouldLog(key, interval, Stopwatch.GetTimestamp());
    }

    internal bool ShouldLog(string key, TimeSpan interval, long nowTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (interval <= TimeSpan.Zero)
        {
            _lastLoggedTicks[key] = nowTicks;
            return true;
        }

        long intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            if (!_lastLoggedTicks.TryGetValue(key, out long lastTicks))
            {
                if (_lastLoggedTicks.TryAdd(key, nowTicks))
                {
                    return true;
                }

                continue;
            }

            if (nowTicks - lastTicks < intervalTicks)
            {
                return false;
            }

            if (_lastLoggedTicks.TryUpdate(key, nowTicks, lastTicks))
            {
                return true;
            }
        }
    }
}
