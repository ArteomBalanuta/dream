using System;
using System.Collections.Generic;

namespace Dream.Rendering.Core;


public class ResourceCache<T>
{
    private readonly Dictionary<string, T> _items = new();

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
}
