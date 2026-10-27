using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace Dream.Rendering.Core;

// Keyed owner of disposable values. Replace stores the new value before disposing the old one,
// so a caller that builds the new value first never loses the old one to a failed build.
public sealed class DisposableRegistry<TKey, T>
    where TKey : notnull
    where T : class, IDisposable
{
    private readonly Dictionary<TKey, T> _items;

    public DisposableRegistry()
    {
        _items = new Dictionary<TKey, T>();
    }

    public DisposableRegistry(IEqualityComparer<TKey>? comparer)
    {
        _items = new Dictionary<TKey, T>(comparer);
    }

    public int Count => _items.Count;

    public IReadOnlyCollection<TKey> Keys => _items.Keys;

    // Throws KeyNotFoundException for an unknown key, like a dictionary.
    public T this[TKey key] => _items[key];

    public bool TryGet(TKey key, [MaybeNullWhen(false)] out T value)
    {
        return _items.TryGetValue(key, out value);
    }

    public void Replace(TKey key, T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _items.TryGetValue(key, out T? previous);
        _items[key] = value;
        if (previous != null && !ReferenceEquals(previous, value))
        {
            previous.Dispose();
        }
    }

    // Empties the registry and disposes every value. Every value is disposed even if an earlier
    // one throws; one failure is rethrown as is, several as an AggregateException. A second call
    // finds the registry empty and does nothing.
    public void DisposeAll()
    {
        T[] snapshot = _items.Values.ToArray();
        _items.Clear();

        List<Exception>? failures = null;
        foreach (T value in snapshot)
        {
            try
            {
                value.Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures == null)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(failures);
    }
}
