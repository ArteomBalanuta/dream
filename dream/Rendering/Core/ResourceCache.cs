using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Dream.Rendering.Core;


// Thread-safe: every member takes one private lock. GetOrAdd runs the factory under that lock,
// so a key is created at most once even when two threads ask for it together; a slow factory
// therefore blocks other callers of the same cache. Release callbacks in Clear run outside it.
public class ResourceCache<T>
{
    private readonly object _sync = new();
    private readonly Dictionary<string, T> _items = new(StringComparer.Ordinal);

    // A snapshot taken under the lock, so enumerating it never races a writer.
    public IReadOnlyCollection<T> Values
    {
        get
        {
            lock (_sync)
            {
                return [.. _items.Values];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Count;
            }
        }
    }

    public T GetOrAdd(string key, Func<string, T> factory)
    {
        lock (_sync)
        {
            if (_items.TryGetValue(key, out T? existing))
            {
                return existing;
            }

            T created = factory(key);
            _items[key] = created;
            return created;
        }
    }

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out T value)
    {
        lock (_sync)
        {
            return _items.TryGetValue(key, out value);
        }
    }

    // Adds the value unless the key is already present; returns false and leaves the cache
    // unchanged in that case, so the caller still owns the value it passed.
    public bool TryAdd(string key, T value)
    {
        lock (_sync)
        {
            return _items.TryAdd(key, value);
        }
    }

    public void Clear(Action<T>? release = null)
    {
        T[] snapshot;
        lock (_sync)
        {
            snapshot = [.. _items.Values];
            _items.Clear();
        }

        if (release == null)
        {
            return;
        }

        // Every item is released even if an earlier release throws; the cache is already empty.
        List<Exception>? failures = null;
        foreach (T value in snapshot)
        {
            try
            {
                release(value);
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

public static class ResourceCacheExtensions
{
    public static void DisposeAll<T>(this ResourceCache<T> cache) where T : IDisposable
    {
        cache.Clear(value => value.Dispose());
    }
}
