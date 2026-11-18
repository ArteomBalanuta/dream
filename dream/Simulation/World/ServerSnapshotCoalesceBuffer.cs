namespace Dream.Simulation.World;

// The merge of ServerSnapshotEnvelope.TryCoalesce, done by appending into reusable lists. Folding n
// snapshots into one with TryCoalesce copies the merged lists every time (O(n^2) and an allocation
// per merge); this buffer appends each snapshot's deltas once, and after warm-up allocates
// nothing. ToEnvelope returns exactly what chaining TryCoalesce over the same snapshots returns:
// the newest snapshot with the accepted command sequences maxed (older keys first, in order) and
// every world mutation and damage event in order.
//
// The returned envelope aliases this buffer's lists, so the owner must not Append, Clear or Reset
// it while that envelope is still being read (encoded or logged).
public sealed class ServerSnapshotCoalesceBuffer
{
    private readonly Dictionary<string, ulong> _sequences = new(StringComparer.Ordinal);
    private readonly List<WorldMutationDelta> _mutations = [];
    private readonly List<DamageEventDelta> _damageEvents = [];
    private readonly List<CastEventDelta> _castEvents = [];
    private ServerSnapshotEnvelope _latest;

    // Cast events accumulate like mutations but are cosmetic: past this many the rest are dropped
    // silently. There is deliberately no overflow flag and no world resync for them.
    public const int MaxCoalescedCastEvents = 1024;

    public bool IsEmpty { get; private set; } = true;

    public int WorldMutationCount => _mutations.Count;

    public int DamageEventCount => _damageEvents.Count;

    public int CastEventCount => _castEvents.Count;

    public ulong ServerTick => _latest.ServerTick;

    // Same rule as TryCoalesce: a snapshot carrying a full world is never merged.
    public static bool CanCoalesce(in ServerSnapshotEnvelope snapshot) => snapshot.World == null;

    // Starts a merge from the first (oldest) snapshot. Throws for a full-world snapshot.
    public void Begin(in ServerSnapshotEnvelope first)
    {
        if (!CanCoalesce(first))
        {
            throw new ArgumentException("A snapshot carrying a full world cannot be coalesced.", nameof(first));
        }

        Reset();
        AddSequences(first.LastAcceptedCommandSequences, takeMaximum: false);
        AddRange(_mutations, first.WorldMutations);
        AddRange(_damageEvents, first.DamageEvents);
        AddCastEvents(first.CastEvents);
        _latest = first;
        IsEmpty = false;
    }

    // Merges a newer snapshot into the pending one. Throws when nothing was begun, or for a
    // full-world snapshot.
    public void Append(in ServerSnapshotEnvelope newer)
    {
        if (IsEmpty)
        {
            throw new InvalidOperationException("Begin must be called before Append.");
        }

        if (!CanCoalesce(newer))
        {
            throw new ArgumentException("A snapshot carrying a full world cannot be coalesced.", nameof(newer));
        }

        AddSequences(newer.LastAcceptedCommandSequences, takeMaximum: true);
        AddRange(_mutations, newer.WorldMutations);
        AddRange(_damageEvents, newer.DamageEvents);
        AddCastEvents(newer.CastEvents);
        _latest = newer;
    }

    // Drops the merged world mutations and damage events, keeping the newest actor state and the
    // accepted command sequences. Used when a lagging connection's backlog overflows and the
    // connection is resynced with a full world instead.
    public void DropDeltas()
    {
        _mutations.Clear();
        _damageEvents.Clear();
        _castEvents.Clear();

        // The checksum covered the deltas we just dropped, so it no longer describes what
        // this snapshot carries. ServerConnection.WithoutDeltas drops it on the unmerged
        // path for the same reason; keeping it here makes the client acknowledge mutation
        // sequences it never applied and then ask for a second resync.
        _latest = _latest with { WorldChecksum = null, WorldChecksumSequence = 0 };
    }

    public ServerSnapshotEnvelope ToEnvelope()
    {
        if (IsEmpty)
        {
            throw new InvalidOperationException("Nothing has been coalesced.");
        }

        return _latest with
        {
            LastAcceptedCommandSequences = _sequences,
            WorldMutations = _mutations,
            DamageEvents = _damageEvents,
            CastEvents = _castEvents.Count == 0 ? null : _castEvents
        };
    }

    // Clears the buffer for reuse, keeping list capacity. Dictionary.Clear keeps insertion order
    // for the next fill because nothing is ever removed individually.
    public void Reset()
    {
        _sequences.Clear();
        _mutations.Clear();
        _damageEvents.Clear();
        _castEvents.Clear();
        _latest = default;
        IsEmpty = true;
    }

    private void AddSequences(IReadOnlyDictionary<string, ulong>? sequences, bool takeMaximum)
    {
        if (sequences == null)
        {
            return;
        }

        // The runtime exports a Dictionary; its struct enumerator avoids boxing on the tick thread.
        if (sequences is Dictionary<string, ulong> dictionary)
        {
            foreach (KeyValuePair<string, ulong> entry in dictionary)
            {
                AddSequence(entry.Key, entry.Value, takeMaximum);
            }

            return;
        }

        foreach (KeyValuePair<string, ulong> entry in sequences)
        {
            AddSequence(entry.Key, entry.Value, takeMaximum);
        }
    }

    private void AddSequence(string sessionId, ulong sequence, bool takeMaximum)
    {
        if (takeMaximum && _sequences.TryGetValue(sessionId, out ulong existing))
        {
            _sequences[sessionId] = Math.Max(existing, sequence);
            return;
        }

        _sequences[sessionId] = sequence;
    }

    private void AddCastEvents(IReadOnlyList<CastEventDelta>? source)
    {
        if (source == null)
        {
            return;
        }

        for (int i = 0; i < source.Count && _castEvents.Count < MaxCoalescedCastEvents; i++)
        {
            _castEvents.Add(source[i]);
        }
    }

    private static void AddRange<T>(List<T> target, IReadOnlyList<T>? source)
    {
        if (source == null)
        {
            return;
        }

        int count = source.Count;
        if (target.Capacity < target.Count + count)
        {
            target.Capacity = Math.Max(target.Count + count, target.Capacity * 2);
        }

        for (int i = 0; i < count; i++)
        {
            target.Add(source[i]);
        }
    }
}
