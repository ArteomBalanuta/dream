using System.Collections.Generic;

public static class SafeDictionaryLookup
{
    public static bool TryGetValue<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> source,
        TKey? key,
        out TValue value)
        where TKey : notnull
    {
        if (key is null)
        {
            value = default!;
            return false;
        }

        return source.TryGetValue(key, out value!);
    }
}
