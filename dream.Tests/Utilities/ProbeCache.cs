using System.Collections.Concurrent;

namespace Dream.Tests.Utilities;

// Runs a deterministic set-up probe once per key for the whole test run (ARCH-1c). Several
// simulation and loopback classes probe the startup world for an open walking direction before
// each test: up to four runtimes built and stepped hundreds of ticks, the same answer every time
// for the same inputs. The probe is set-up, not an assertion, so sharing its result changes no
// test's coverage; it only stops each test (and each split collection running at once) from
// paying for it again. Concurrent callers of one key wait for the single computation. A probe
// that throws throws to every caller of that key, as it would have thrown in each of them.
internal sealed class ProbeCache<TKey, TValue>(Func<TKey, TValue> probe)
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<TValue>> _results = new();

    public TValue Get(TKey key)
    {
        return _results.GetOrAdd(key, k => new Lazy<TValue>(() => probe(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}
