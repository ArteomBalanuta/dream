using OpenTK.Mathematics;

namespace Dream.Rendering.Effects;

// A private xorshift32 for particle jitter. Client-only and never shared: effects must not draw
// from the simulation's random streams (that would make a visual choice change gameplay), and
// System.Random is avoided so a fixed seed replays identically on every runtime.
public struct FxRandom
{
    public const uint DefaultSeed = 0x9E3779B9u;

    private uint _state;

    public FxRandom(uint seed)
    {
        _state = seed == 0u ? DefaultSeed : seed;
    }

    public uint NextUInt()
    {
        uint x = _state == 0u ? DefaultSeed : _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    // [0, 1).
    public float NextFloat()
    {
        return (NextUInt() >> 8) * (1f / (1 << 24));
    }

    public float Range(float min, float max)
    {
        return min + (max - min) * NextFloat();
    }

    public int Range(int minInclusive, int maxExclusive)
    {
        return minInclusive + (int)(NextUInt() % (uint)(maxExclusive - minInclusive));
    }

    // Uniform on the unit sphere (height and azimuth, so no rejection loop).
    public Vector3 UnitVector()
    {
        float z = Range(-1f, 1f);
        float azimuth = Range(0f, MathHelper.TwoPi);
        float ring = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(ring * MathF.Cos(azimuth), z, ring * MathF.Sin(azimuth));
    }
}
