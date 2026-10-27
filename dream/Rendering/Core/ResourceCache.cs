using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace Dream.Rendering.Core;


public class ResourceCache<T>
{
    private readonly Dictionary<string, T> _items = new();

    public IReadOnlyCollection<T> Values => _items.Values;

    public T GetOrAdd(string key, Func<string, T> factory)
    {
        if (_items.TryGetValue(key, out T? existing))
        {
            return existing;
        }

        T created = factory(key);
        _items[key] = created;
        return created;
    }

    public void Clear(Action<T>? release = null)
    {
        T[] snapshot = _items.Values.ToArray();
        _items.Clear();

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
