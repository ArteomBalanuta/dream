using System.Net;
using Dream.Multiplayer.Protocol;

namespace Dream.Server;

// Join throttle state (NETR-8), owned by DedicatedServerHost and touched only under its tick lock.
//
// A join that a live holder rejects (its session id or player name is held by a connection heard
// from inside the takeover window) throttles further joins for the same contested session id or
// player name from the same source. The source is the remote IP address parsed from the
// connection id, which TcpClientTransport sets to the socket's remote endpoint ("ip:port"); a
// connection id that is not an IP endpoint (in-memory and test transports) is its own source.
//
// Keying by source rather than by connection means reconnecting does not reset the throttle, so
// each spam attempt no longer costs a fresh connection's deferred join a persisted-player
// prefetch (a database read). Keying also by the contested session id or name means clients that
// share an address (NAT, or a loopback test server with many clients) never throttle each other
// unless they contest the same seat, and a spammer cannot lock a victim out of their own name
// from the victim's address, which a name-only key would.
//
// The interval starts at ServerRoutingContext.JoinThrottleInitialInterval and doubles on every
// further live-holder rejection up to JoinThrottleMaxInterval; an accepted join from the source
// clears its entries. An entry is forgotten once JoinThrottleMaxInterval has passed after its
// interval ended (pruned when a new entry is added to a table of PruneThreshold or more, and on
// lookup), and the table holds at most MaxEntries entries (past that, the entry whose interval
// ended earliest is evicted), so its size is bounded whatever the traffic.
internal sealed class JoinThrottleTable
{
    internal const int MaxEntries = 1024;

    // A new entry first prunes expired ones once the table holds this many.
    private const int PruneThreshold = 64;

    private readonly Dictionary<Key, Entry> _entries = new(KeyComparer.Instance);

    public int Count => _entries.Count;

    // The source a connection's joins are throttled by: the remote IP address when the connection
    // id is an "ip:port" endpoint (IPv4-mapped IPv6 folded to IPv4), else the connection id.
    public static string ResolveSource(string connectionId)
    {
        if (IPEndPoint.TryParse(connectionId, out IPEndPoint? endpoint)
            && endpoint.Port != 0
            && string.Equals(endpoint.ToString(), connectionId, StringComparison.OrdinalIgnoreCase))
        {
            IPAddress address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
            return address.ToString();
        }

        return connectionId;
    }

    public static Key SessionKey(string source, string sessionId) => new(source, IsSession: true, sessionId);

    public static Key PlayerNameKey(string source, string playerName) => new(source, IsSession: false, playerName);

    // Whether the key is inside its throttle interval. An expired entry is removed.
    public bool IsThrottled(Key key, long nowTimestamp, long timestampFrequency)
    {
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            return false;
        }

        if (IsExpired(entry, nowTimestamp, timestampFrequency))
        {
            _entries.Remove(key);
            return false;
        }

        return nowTimestamp < entry.UntilTimestamp;
    }

    // Counts a dropped join against the key's current interval and returns how many it has
    // dropped so far (0 when the key has no entry).
    public int RecordDroppedJoinAttempt(Key key)
    {
        return _entries.TryGetValue(key, out Entry? entry) ? ++entry.DroppedJoinAttempts : 0;
    }

    // Starts the key's next interval (doubled, capped) and returns its length in timestamp ticks;
    // droppedLastInterval is how many joins the previous interval dropped.
    public long Throttle(Key key, long nowTimestamp, long timestampFrequency, out int droppedLastInterval)
    {
        long initialTicks = ToTicks(ServerRoutingContext.JoinThrottleInitialInterval, timestampFrequency);
        long maxTicks = ToTicks(ServerRoutingContext.JoinThrottleMaxInterval, timestampFrequency);
        if (_entries.TryGetValue(key, out Entry? entry) && IsExpired(entry, nowTimestamp, timestampFrequency))
        {
            _entries.Remove(key);
            entry = null;
        }

        if (entry == null)
        {
            MakeRoom(nowTimestamp, timestampFrequency);
            entry = new Entry();
            _entries[key] = entry;
        }

        entry.IntervalTicks = entry.IntervalTicks == 0 ? initialTicks : Math.Min(entry.IntervalTicks * 2, maxTicks);
        entry.UntilTimestamp = nowTimestamp + entry.IntervalTicks;
        droppedLastInterval = entry.DroppedJoinAttempts;
        entry.DroppedJoinAttempts = 0;
        return entry.IntervalTicks;
    }

    public void Reset(Key key)
    {
        _entries.Remove(key);
    }

    // Seconds left in the key's interval, rounded up; 0 when it is not throttled.
    public int SecondsLeft(Key key, long nowTimestamp, long timestampFrequency)
    {
        return _entries.TryGetValue(key, out Entry? entry) && nowTimestamp < entry.UntilTimestamp
            ? (int)Math.Ceiling((entry.UntilTimestamp - nowTimestamp) / (double)timestampFrequency)
            : 0;
    }

    public void PruneExpired(long nowTimestamp, long timestampFrequency)
    {
        List<Key>? expired = null;
        foreach (KeyValuePair<Key, Entry> pair in _entries)
        {
            if (IsExpired(pair.Value, nowTimestamp, timestampFrequency))
            {
                (expired ??= []).Add(pair.Key);
            }
        }

        if (expired == null)
        {
            return;
        }

        foreach (Key key in expired)
        {
            _entries.Remove(key);
        }
    }

    private void MakeRoom(long nowTimestamp, long timestampFrequency)
    {
        if (_entries.Count < PruneThreshold)
        {
            return;
        }

        PruneExpired(nowTimestamp, timestampFrequency);
        while (_entries.Count >= MaxEntries)
        {
            Key oldest = default;
            long oldestUntil = long.MaxValue;
            foreach (KeyValuePair<Key, Entry> pair in _entries)
            {
                if (pair.Value.UntilTimestamp < oldestUntil)
                {
                    oldestUntil = pair.Value.UntilTimestamp;
                    oldest = pair.Key;
                }
            }

            _entries.Remove(oldest);
        }
    }

    // Forgotten once a full max interval has passed since the entry's interval ended.
    private static bool IsExpired(Entry entry, long nowTimestamp, long timestampFrequency)
    {
        return nowTimestamp - entry.UntilTimestamp >= ToTicks(ServerRoutingContext.JoinThrottleMaxInterval, timestampFrequency);
    }

    private static long ToTicks(TimeSpan interval, long timestampFrequency)
    {
        return (long)(interval.TotalSeconds * timestampFrequency);
    }

    internal readonly record struct Key(string Source, bool IsSession, string Value);

    private sealed class Entry
    {
        public long IntervalTicks;
        public long UntilTimestamp;
        public int DroppedJoinAttempts;
    }

    // Session ids compare ordinally (TryClaimSession); player names case-insensitively, like the
    // runtime's duplicate-display-name check.
    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(Key x, Key y)
        {
            return x.IsSession == y.IsSession
                && string.Equals(x.Source, y.Source, StringComparison.Ordinal)
                && string.Equals(x.Value, y.Value, x.IsSession ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(Key key)
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(key.Source),
                key.IsSession,
                key.IsSession ? StringComparer.Ordinal.GetHashCode(key.Value) : StringComparer.OrdinalIgnoreCase.GetHashCode(key.Value));
        }
    }
}
