using System.Collections;

namespace Dream.Simulation.Physics;

// A set that iterates in insertion order, exactly like a List<T> that only ever Adds at the end
// and Removes by value, but with O(1) Add, Contains and Remove. Iteration order feeds the
// simulation (the order bodies are synced, removed from Bullet and checksummed), so it must stay
// identical to the List it replaces: a removal leaves a tombstone instead of shifting the tail,
// and compaction later closes the gaps without reordering. Compaction runs when tombstones
// outnumber half the live items, or on the first indexed read after a removal, so indexing stays
// O(1) amortized. Enumeration skips tombstones and never compacts.
//
// Every Add gets a sequence number that increases for the set's lifetime. Callers can remember
// NextSequence and later find exactly the items added since (they sit at the tail).
// RemovedCount counts removals, so callers can tell whether anything left the set since they looked.
public sealed class InsertionOrderedSet<T> : IReadOnlyList<T>
    where T : class
{
    private T?[] _items;
    private long[] _sequences;
    private readonly Dictionary<T, int> _slots;
    private int _end;
    private int _tombstones;
    // Bumped by every change an enumerator cannot survive: Add, Remove, Clear and Compact (which
    // moves items under it). An enumerator throws when it sees a different stamp, like List<T>'s.
    private int _version;

    public InsertionOrderedSet()
        : this(0)
    {
    }

    public InsertionOrderedSet(int capacity)
    {
        _items = capacity == 0 ? [] : new T?[capacity];
        _sequences = capacity == 0 ? [] : new long[capacity];
        _slots = new Dictionary<T, int>(capacity, ReferenceEqualityComparer.Instance);
    }

    public int Count => _end - _tombstones;

    // The sequence number the next Add will receive.
    public long NextSequence { get; private set; }

    // Total removals over the set's lifetime (Clear counts each item it removes).
    public long RemovedCount { get; private set; }

    // Physical slots in use, tombstones included: the bound for SlotAt.
    internal int SlotCount => _end;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (_tombstones > 0)
            {
                Compact();
            }

            return _items[index]!;
        }
    }

    public void Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!TryAdd(item))
        {
            throw new ArgumentException("The item is already in the set.", nameof(item));
        }
    }

    // List<T>.Add allowed duplicates; nothing adds a body or cube twice, and a set cannot hold
    // one item in two places, so a duplicate add is rejected rather than silently reordering.
    public bool TryAdd(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_slots.ContainsKey(item))
        {
            return false;
        }

        if (_end == _items.Length)
        {
            Grow();
        }

        _items[_end] = item;
        _sequences[_end] = NextSequence++;
        _slots.Add(item, _end);
        _end++;
        _version++;
        return true;
    }

    public bool Contains(T item)
    {
        return item != null && _slots.ContainsKey(item);
    }

    public bool Remove(T item)
    {
        if (item == null || !_slots.Remove(item, out int slot))
        {
            return false;
        }

        RemovedCount++;
        _version++;
        if (slot == _end - 1)
        {
            // Removing the tail needs no tombstone.
            _items[slot] = null;
            _end--;
            TrimTrailingTombstones();
        }
        else
        {
            _items[slot] = null;
            _tombstones++;
            if (_tombstones > 32 && _tombstones > Count / 2)
            {
                Compact();
            }
        }

        return true;
    }

    public void Clear()
    {
        RemovedCount += Count;
        Array.Clear(_items, 0, _end);
        _slots.Clear();
        _end = 0;
        _tombstones = 0;
        _version++;
    }

    // The item in physical slot `slot` (0 <= slot < SlotCount), or null for a tombstone, with its
    // add sequence. Slots are in insertion order.
    internal T? SlotAt(int slot, out long sequence)
    {
        sequence = _sequences[slot];
        return _items[slot];
    }

    // The physical slot of an item, for SlotAt. Valid until the next Remove or indexed read.
    internal bool TryGetSlot(T item, out int slot)
    {
        return _slots.TryGetValue(item, out slot);
    }

    public Enumerator GetEnumerator()
    {
        return new Enumerator(this);
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private void Grow()
    {
        int capacity = _items.Length == 0 ? 16 : _items.Length * 2;
        Array.Resize(ref _items, capacity);
        Array.Resize(ref _sequences, capacity);
    }

    private void TrimTrailingTombstones()
    {
        while (_end > 0 && _items[_end - 1] == null)
        {
            _end--;
            _tombstones--;
        }
    }

    // Closes the gaps in place, keeping relative order, and re-points the slot index.
    private void Compact()
    {
        int write = 0;
        for (int read = 0; read < _end; read++)
        {
            T? item = _items[read];
            if (item == null)
            {
                continue;
            }

            if (write != read)
            {
                _items[write] = item;
                _sequences[write] = _sequences[read];
                _slots[item] = write;
            }

            write++;
        }

        Array.Clear(_items, write, _end - write);
        _end = write;
        _tombstones = 0;
        _version++;
    }

    private static void ThrowModifiedDuringEnumeration()
    {
        throw new InvalidOperationException("The set was modified; enumeration operation may not execute.");
    }

    // Skips tombstones. Like List<T>'s enumerator it must not outlive a mutation of the set: Add,
    // Remove, Clear, or an indexed read that compacts, make the next MoveNext or Reset throw.
    public struct Enumerator : IEnumerator<T>
    {
        private readonly InsertionOrderedSet<T> _set;
        private readonly int _version;
        private int _slot;
        private T? _current;

        internal Enumerator(InsertionOrderedSet<T> set)
        {
            _set = set;
            _version = set._version;
            _slot = -1;
            _current = null;
        }

        public readonly T Current => _current!;

        readonly object IEnumerator.Current => _current!;

        public bool MoveNext()
        {
            if (_version != _set._version)
            {
                ThrowModifiedDuringEnumeration();
            }

            T?[] items = _set._items;
            int end = _set._end;
            while (++_slot < end)
            {
                T? item = items[_slot];
                if (item != null)
                {
                    _current = item;
                    return true;
                }
            }

            _current = null;
            return false;
        }

        public void Reset()
        {
            if (_version != _set._version)
            {
                ThrowModifiedDuringEnumeration();
            }

            _slot = -1;
            _current = null;
        }

        public readonly void Dispose()
        {
        }
    }
}
