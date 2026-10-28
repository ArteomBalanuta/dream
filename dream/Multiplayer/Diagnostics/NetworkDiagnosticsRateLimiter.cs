using System.Collections.Concurrent;
using System.Diagnostics;

namespace Dream.Multiplayer.Diagnostics;

// Remembers when each key last logged. Keys include per-connection and per-actor ones
// ("server.rx.command.<connection>"), so on a long-running process the set of keys is unbounded
// (SCALE-7). A key is forgotten once its last log is older than the longest interval ever asked
// for: by then ShouldLog would let it through anyway, so forgetting it changes nothing. Expired
// keys are swept when a new key is added to a table that has grown to the next sweep size, and
// the table never holds more than MaxEntries keys (past that the oldest are dropped, which at
// worst lets one extra line through for a key still in use).
public sealed class NetworkDiagnosticsRateLimiter
{
    internal const int MaxEntries = 4096;

    // A new key first sweeps expired ones once the table holds this many.
    private const int MinSweepCount = 64;

    private readonly ConcurrentDictionary<string, long> _lastLoggedTicks = new(StringComparer.Ordinal);
    private long _longestIntervalTicks;
    private int _sweepAtCount = MinSweepCount;
    private int _sweeping;

    internal int Count => _lastLoggedTicks.Count;

    public bool ShouldLog(string key, TimeSpan interval)
    {
        return ShouldLog(key, interval, Stopwatch.GetTimestamp());
    }

    internal bool ShouldLog(string key, TimeSpan interval, long nowTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (interval <= TimeSpan.Zero)
        {
            if (_lastLoggedTicks.TryGetValue(key, out _))
            {
                _lastLoggedTicks[key] = nowTicks;
            }
            else
            {
                AddKey(key, nowTicks);
            }

            return true;
        }

        long intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        RaiseLongestInterval(intervalTicks);
        while (true)
        {
            if (!_lastLoggedTicks.TryGetValue(key, out long lastTicks))
            {
                if (AddKey(key, nowTicks))
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

    // Forgets every key whose last log is at least the longest interval old.
    internal void SweepExpired(long nowTicks)
    {
        long horizon = Volatile.Read(ref _longestIntervalTicks);
        foreach (KeyValuePair<string, long> pair in _lastLoggedTicks)
        {
            if (nowTicks - pair.Value >= horizon)
            {
                // Only if it has not logged since it was read.
                _lastLoggedTicks.TryRemove(pair);
            }
        }
    }

    private bool AddKey(string key, long nowTicks)
    {
        if (!_lastLoggedTicks.TryAdd(key, nowTicks))
        {
            return false;
        }

        if (_lastLoggedTicks.Count >= Volatile.Read(ref _sweepAtCount)
            && Interlocked.Exchange(ref _sweeping, 1) == 0)
        {
            try
            {
                SweepExpired(nowTicks);
                EvictOldestOverCap();
                Volatile.Write(ref _sweepAtCount, Math.Max(MinSweepCount, Math.Min(MaxEntries, _lastLoggedTicks.Count * 2)));
            }
            finally
            {
                Volatile.Write(ref _sweeping, 0);
            }
        }

        return true;
    }

    private void EvictOldestOverCap()
    {
        int excess = _lastLoggedTicks.Count - MaxEntries;
        if (excess < 0)
        {
            return;
        }

        // Rare (more than MaxEntries keys inside one interval): drop the oldest quarter at once so
        // the next sweep is far off.
        KeyValuePair<string, long>[] entries = _lastLoggedTicks.ToArray();
        Array.Sort(entries, static (a, b) => a.Value.CompareTo(b.Value));
        int toRemove = Math.Min(entries.Length, excess + (MaxEntries / 4));
        for (int i = 0; i < toRemove; i++)
        {
            _lastLoggedTicks.TryRemove(entries[i]);
        }
    }

    private void RaiseLongestInterval(long intervalTicks)
    {
        long current = Volatile.Read(ref _longestIntervalTicks);
        while (intervalTicks > current)
        {
            long seen = Interlocked.CompareExchange(ref _longestIntervalTicks, intervalTicks, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
