using System.Collections;

namespace Dream.Simulation.World.Geometry;

// Read-only view of two lists back to back, so a cached list and a per-frame list can be exported
// together without copying either.
internal sealed class ConcatenatedReadOnlyList<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> _first;
    private readonly IReadOnlyList<T> _second;

    public ConcatenatedReadOnlyList(IReadOnlyList<T> first, IReadOnlyList<T> second)
    {
        _first = first;
        _second = second;
    }

    public int Count => _first.Count + _second.Count;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return index < _first.Count ? _first[index] : _second[index - _first.Count];
        }
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

    public struct Enumerator : IEnumerator<T>
    {
        private readonly ConcatenatedReadOnlyList<T> _list;
        private int _index;

        internal Enumerator(ConcatenatedReadOnlyList<T> list)
        {
            _list = list;
            _index = -1;
        }

        public readonly T Current => _list[_index];

        readonly object? IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_index + 1 >= _list.Count)
            {
                _index = _list.Count;
                return false;
            }

            _index++;
            return true;
        }

        public void Reset()
        {
            _index = -1;
        }

        public readonly void Dispose()
        {
        }
    }
}
