using System.Runtime.CompilerServices;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Characters;
using Dream.Server;
using Dream.Simulation.Persistence;

namespace Dream.Multiplayer.Protocol;

// M1-C. Answers ClientCharacterListRequest before a join: the characters of the account the token
// belongs to. The token is resolved to an AccountKey (a hash) at once, exactly as a join does; the
// raw token is never queued, stored or logged. The read is queued behind the writes already queued on
// the persistence worker, never run on the tick or receive thread, and the answer is handed to
// Deliver (the host turns it into a pending connection reply). A host without accounts, a bad token
// or a failed read all answer with an empty list, so the reply never tells a bad token from an
// account without characters, and only the requesting account's own characters are ever read.
public sealed class CharacterListHandler : IServerMessageHandler
{
    // A connection gets at most one list per interval; requests inside it (or while one is being read)
    // are dropped unanswered.
    public static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(2);

    // Per source address (the remote IP, as for the join throttle): a burst of SourceBurst lists, then one
    // per RequestInterval, so reconnecting does not get a fresh allowance. At most MaxPendingReads reads
    // are queued or running at once across all sources; the source table holds at most MaxSources entries.
    public const int SourceBurst = 3;
    public const int MaxPendingReads = 16;
    public const int MaxSources = 1024;

    private readonly object _sourceSync = new();
    private readonly Dictionary<string, SourceBucket> _sources = new(StringComparer.Ordinal);
    private int _pendingReads;
    private readonly TimeProvider _timeProvider;
    private readonly ConditionalWeakTable<ServerRoutingContext, ConnectionState> _states = new();
    private IAccountRepository? _accounts;
    private PersistenceWriteQueue? _writes;

    public CharacterListHandler()
        : this(TimeProvider.System)
    {
    }

    public CharacterListHandler(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public string MessageKind => ClientCharacterListRequest.Kind;

    // Where a read's answer goes: the connection id and the message. Set by the host that owns the handler.
    public Action<string, IServerToClientMessage>? Deliver { get; set; }

    // The account repository and the persistence queue the read runs on. Without accounts the list is empty.
    public void Attach(IAccountRepository? accounts, PersistenceWriteQueue? writes)
    {
        _accounts = accounts;
        _writes = writes;
    }

    public void Handle(ServerRoutingContext context, IClientToServerMessage message)
    {
        ClientCharacterListRequest request = (ClientCharacterListRequest)message;
        if (context.LastJoinRequest != null)
        {
            return;
        }

        IAccountRepository? accounts = _accounts;
        if (accounts == null || !AccountKey.TryFromToken(request.AccountToken, out AccountKey account))
        {
            context.OutboundMessages.Add(Empty());
            return;
        }

        ConnectionState state = _states.GetValue(context, static _ => new ConnectionState());
        long now = _timeProvider.GetTimestamp();
        lock (state)
        {
            if (state.Busy
                || (state.HasRequested
                    && now - state.LastRequest < (long)(RequestInterval.TotalSeconds * _timeProvider.TimestampFrequency)))
            {
                return;
            }

            if (!TryTakeSource(JoinThrottleTable.ResolveSource(context.ConnectionId), now))
            {
                return;
            }

            if (Interlocked.Increment(ref _pendingReads) > MaxPendingReads)
            {
                Interlocked.Decrement(ref _pendingReads);
                return;
            }

            state.HasRequested = true;
            state.LastRequest = now;
            state.Busy = true;
        }

        string connectionId = context.ConnectionId;
        Action<string, IServerToClientMessage>? deliver = Deliver;

        void Read()
        {
            ServerCharacterList reply = Empty();
            try
            {
                IReadOnlyList<AccountCharacterSummary> found = accounts.ListCharacters(account, ServerCharacterList.MaxCharacters);
                int count = Math.Min(found.Count, ServerCharacterList.MaxCharacters);
                CharacterListEntry[] entries = new CharacterListEntry[count];
                for (int i = 0; i < count; i++)
                {
                    entries[i] = new CharacterListEntry(found[i].DisplayName, found[i].Level);
                }

                reply = new ServerCharacterList(entries);
            }
            catch (Exception ex)
            {
                NetworkDiagnosticsLog.Write("server.character-list.error", $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                lock (state)
                {
                    state.Busy = false;
                }

                Interlocked.Decrement(ref _pendingReads);
            }

            deliver?.Invoke(connectionId, reply);
        }

        PersistenceWriteQueue? writes = _writes;
        if (writes == null)
        {
            Read();
        }
        else if (!writes.EnqueueRead(Read))
        {
            lock (state)
            {
                state.Busy = false;
            }

            Interlocked.Decrement(ref _pendingReads);
            deliver?.Invoke(connectionId, Empty());
        }
    }

    private bool TryTakeSource(string source, long now)
    {
        long frequency = _timeProvider.TimestampFrequency;
        double refillSeconds = RequestInterval.TotalSeconds;
        lock (_sourceSync)
        {
            if (!_sources.TryGetValue(source, out SourceBucket? bucket))
            {
                if (_sources.Count >= MaxSources)
                {
                    List<string> full = [];
                    foreach (KeyValuePair<string, SourceBucket> pair in _sources)
                    {
                        if (pair.Value.Tokens + Math.Max(0, now - pair.Value.Last) / (double)frequency / refillSeconds >= SourceBurst)
                        {
                            full.Add(pair.Key);
                        }
                    }

                    foreach (string key in full)
                    {
                        _sources.Remove(key);
                    }

                    if (_sources.Count >= MaxSources)
                    {
                        // Every source is busy: drop the request rather than grow the table.
                        return false;
                    }
                }

                bucket = new SourceBucket { Tokens = SourceBurst, Last = now };
                _sources[source] = bucket;
            }
            else
            {
                bucket.Tokens = Math.Min(SourceBurst, bucket.Tokens + Math.Max(0, now - bucket.Last) / (double)frequency / refillSeconds);
                bucket.Last = now;
            }

            if (bucket.Tokens < 1.0)
            {
                return false;
            }

            bucket.Tokens -= 1.0;
            return true;
        }
    }

    private sealed class SourceBucket
    {
        public double Tokens;
        public long Last;
    }

    private static ServerCharacterList Empty() => new([]);

    private sealed class ConnectionState
    {
        public bool Busy;
        public bool HasRequested;
        public long LastRequest;
    }
}
