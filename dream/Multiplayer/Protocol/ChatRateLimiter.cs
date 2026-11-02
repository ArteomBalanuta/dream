namespace Dream.Multiplayer.Protocol;

// M1 fix. Chat token buckets keyed by account (or, on a host without accounts, session id), owned by
// the host so a reconnect does not refill the bucket. Same bucket as ServerRoutingContext: burst
// ChatBurst, refilling ChatRefillPerSecond. The table holds at most MaxEntries buckets; past that,
// buckets that have refilled completely are dropped (they would be full anyway), else the one
// touched longest ago.
public sealed class ChatRateLimiter
{
    public const int MaxEntries = 4096;

    private readonly object _sync = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _buckets.Count;
            }
        }
    }

    public bool TryConsume(string key, long nowTimestamp, long timestampFrequency)
    {
        lock (_sync)
        {
            if (!_buckets.TryGetValue(key, out Bucket? bucket))
            {
                if (_buckets.Count >= MaxEntries)
                {
                    Evict(nowTimestamp, timestampFrequency);
                }

                bucket = new Bucket { Tokens = ServerRoutingContext.ChatBurst, LastRefill = nowTimestamp };
                _buckets[key] = bucket;
            }
            else
            {
                double elapsed = Math.Max(0, nowTimestamp - bucket.LastRefill) / (double)timestampFrequency;
                bucket.Tokens = Math.Min(ServerRoutingContext.ChatBurst, bucket.Tokens + elapsed * ServerRoutingContext.ChatRefillPerSecond);
                bucket.LastRefill = nowTimestamp;
            }

            if (bucket.Tokens < 1.0)
            {
                return false;
            }

            bucket.Tokens -= 1.0;
            return true;
        }
    }

    private void Evict(long nowTimestamp, long timestampFrequency)
    {
        string? oldestKey = null;
        long oldest = long.MaxValue;
        List<string>? full = null;
        foreach (KeyValuePair<string, Bucket> pair in _buckets)
        {
            double elapsed = Math.Max(0, nowTimestamp - pair.Value.LastRefill) / (double)timestampFrequency;
            if (pair.Value.Tokens + elapsed * ServerRoutingContext.ChatRefillPerSecond >= ServerRoutingContext.ChatBurst)
            {
                (full ??= []).Add(pair.Key);
            }
            else if (pair.Value.LastRefill < oldest)
            {
                oldest = pair.Value.LastRefill;
                oldestKey = pair.Key;
            }
        }

        if (full != null)
        {
            foreach (string key in full)
            {
                _buckets.Remove(key);
            }
        }
        else if (oldestKey != null)
        {
            _buckets.Remove(oldestKey);
        }
    }

    private sealed class Bucket
    {
        public double Tokens;
        public long LastRefill;
    }
}
