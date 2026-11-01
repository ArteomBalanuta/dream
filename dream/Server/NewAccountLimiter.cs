using System.Globalization;
using System.Net;

namespace Dream.Server;

// Per-source limit on creating NEW accounts (a join whose token owns no character yet). The 5 characters
// per token cap is bypassed by minting tokens, so one address is also held to a small burst of new
// accounts and then a few per minute (a token bucket per source address). Existing accounts are never
// limited, and loopback sources are exempt (local play, the perf harness and tests use them).
//
// Memory only, touched under the host's tick lock: no database access. The table holds at most
// MaxEntries sources (full buckets are dropped first, then the least recently used), so its size is
// bounded whatever the traffic. A burst of 0 or a rate of 0 turns the limit off.
internal sealed class NewAccountLimiter
{
    public const string BurstEnvironmentVariable = "DREAM_NEW_ACCOUNT_BURST";
    public const string PerMinuteEnvironmentVariable = "DREAM_NEW_ACCOUNTS_PER_MINUTE";
    public const int DefaultBurst = 5;
    public const int DefaultPerMinute = 3;
    public const int MaxEntries = 1024;
    private const int MaxSetting = 100_000;

    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public NewAccountLimiter(int burst = DefaultBurst, int perMinute = DefaultPerMinute)
    {
        Burst = Math.Clamp(burst, 0, MaxSetting);
        PerMinute = Math.Clamp(perMinute, 0, MaxSetting);
    }

    public int Burst { get; }

    public int PerMinute { get; }

    public int Count => _buckets.Count;

    public bool IsEnabled => Burst > 0 && PerMinute > 0;

    // An unset, blank or invalid value keeps the default.
    public static NewAccountLimiter FromEnvironment() => new(
        ReadSetting(BurstEnvironmentVariable, DefaultBurst),
        ReadSetting(PerMinuteEnvironmentVariable, DefaultPerMinute));

    private static int ReadSetting(string name, int fallback)
    {
        string? text = Environment.GetEnvironmentVariable(name);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0
            ? value
            : fallback;
    }

    public static bool IsExempt(string source) => IPAddress.TryParse(source, out IPAddress? address) && IPAddress.IsLoopback(address);

    // 0 when the source may create an account now, else the whole seconds until it may.
    public int SecondsUntilAllowed(string source, long now, long frequency)
    {
        if (!IsEnabled || IsExempt(source) || !_buckets.TryGetValue(source, out Bucket? bucket))
        {
            return 0;
        }

        Refill(bucket, now, frequency);
        if (bucket.Tokens >= 1d)
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling((1d - bucket.Tokens) * 60d / PerMinute));
    }

    // Counts one created account against the source.
    public void Record(string source, long now, long frequency)
    {
        if (!IsEnabled || IsExempt(source))
        {
            return;
        }

        if (!_buckets.TryGetValue(source, out Bucket? bucket))
        {
            MakeRoom(now, frequency);
            bucket = new Bucket { Tokens = Burst, LastTimestamp = now };
            _buckets[source] = bucket;
        }

        Refill(bucket, now, frequency);
        bucket.Tokens = Math.Max(0d, bucket.Tokens - 1d);
    }

    private void Refill(Bucket bucket, long now, long frequency)
    {
        if (now > bucket.LastTimestamp)
        {
            double minutes = (now - bucket.LastTimestamp) / (double)frequency / 60d;
            bucket.Tokens = Math.Min(Burst, bucket.Tokens + minutes * PerMinute);
            bucket.LastTimestamp = now;
        }
    }

    private void MakeRoom(long now, long frequency)
    {
        if (_buckets.Count < MaxEntries)
        {
            return;
        }

        List<string> full = [];
        foreach (KeyValuePair<string, Bucket> pair in _buckets)
        {
            Refill(pair.Value, now, frequency);
            if (pair.Value.Tokens >= Burst)
            {
                full.Add(pair.Key);
            }
        }

        foreach (string key in full)
        {
            _buckets.Remove(key);
        }

        while (_buckets.Count >= MaxEntries)
        {
            string? oldest = null;
            long oldestTimestamp = long.MaxValue;
            foreach (KeyValuePair<string, Bucket> pair in _buckets)
            {
                if (pair.Value.LastTimestamp < oldestTimestamp)
                {
                    oldestTimestamp = pair.Value.LastTimestamp;
                    oldest = pair.Key;
                }
            }

            _buckets.Remove(oldest!);
        }
    }

    private sealed class Bucket
    {
        public double Tokens;
        public long LastTimestamp;
    }
}
