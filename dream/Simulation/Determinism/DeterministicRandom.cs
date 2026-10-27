namespace Dream.Simulation.Determinism;

// SplitMix64 generator for simulation code that must produce the same values on every process,
// machine and runtime version. System.Random's algorithm is not contractually stable, so it must
// not drive anything the server and clients both reproduce.
public sealed class DeterministicRandom
{
    private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

    private ulong _state;

    public DeterministicRandom(ulong seed)
    {
        _state = seed;
    }

    public ulong NextUInt64()
    {
        _state += GoldenGamma;
        return Seed.Mix(_state);
    }

    public int NextInt(int maxExclusive)
    {
        return NextInt(0, maxExclusive);
    }

    // Unbiased: rejects the low tail of the 64-bit range that would over-represent small values.
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxExclusive),
                $"maxExclusive ({maxExclusive}) must be greater than minInclusive ({minInclusive}).");
        }

        ulong range = (ulong)((long)maxExclusive - minInclusive);
        ulong threshold = (0UL - range) % range;
        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value < threshold);

        return (int)((long)minInclusive + (long)(value % range));
    }

    // Uniform in [0, 1): the top 53 bits scaled by 2^-53.
    public double NextDouble()
    {
        return (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    }

    // Uniform in [0, 1): the top 24 bits scaled by 2^-24.
    public float NextSingle()
    {
        return (NextUInt64() >> 40) * (1f / (1 << 24));
    }
}

public static class Seed
{
    private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

    // SplitMix64 finalizer.
    public static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    // Order-sensitive: Combine(a, b) and Combine(b, a) give different seeds.
    public static ulong Combine(ulong a, ulong b)
    {
        return Mix(a ^ Mix(b + GoldenGamma));
    }

    public static ulong Combine(ulong a, ulong b, ulong c)
    {
        return Combine(Combine(a, b), c);
    }

    public static ulong Combine(ulong a, ulong b, ulong c, ulong d)
    {
        return Combine(Combine(Combine(a, b), c), d);
    }

    public static ulong FromCell(int x, int y, int z)
    {
        return Combine(ToSeedComponent(x), ToSeedComponent(y), ToSeedComponent(z));
    }

    private static ulong ToSeedComponent(int value)
    {
        return unchecked((ulong)(long)value);
    }
}

// string.GetHashCode is randomized per process and must never be used for simulation seeds;
// use StableHash.Of for any seed derived from a name.
public static class StableHash
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    // FNV-1a 64 over UTF-16 code units (not UTF-8 bytes), so it is independent of encoding.
    public static ulong Of(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        ulong hash = FnvOffsetBasis;
        foreach (char codeUnit in value)
        {
            hash ^= codeUnit;
            hash *= FnvPrime;
        }

        return hash;
    }
}
