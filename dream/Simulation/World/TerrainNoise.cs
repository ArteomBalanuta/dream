namespace Dream.Simulation.World;

// Deterministic, allocation-free noise for world generation.
//
// Every value comes from integer hashing and float arithmetic only: no Random, no double, no
// transcendental call, and nothing that depends on collection enumeration order. The same
// coordinates must give the same bytes on every machine, every run and every architecture,
// because a world row names the generator that built it and both the server and the client
// rebuild that world from it. Do not expect Map.WorldChecksum to catch a mistake here: it skips
// floor cells entirely (WorldStateChecksum.TryHashCell returns false for Key.Y <= 0), which is
// most of what a material policy decides, so a divergence shows up as the wrong ground on screen
// rather than as a desync.
public static class TerrainNoise
{
    // Eight unit gradients. A fixed table keeps gradient noise free of MathF.Sin/MathF.Cos, whose
    // last bit is not guaranteed to agree across platforms.
    private const float Diagonal = 0.70710678f;

    // ReadOnlySpan over a constant initialiser reads straight from the data section: no array to
    // allocate at class init and nothing for reflection to mutate.
    private static ReadOnlySpan<float> GradientX =>
        [1f, -1f, 0f, 0f, Diagonal, -Diagonal, Diagonal, -Diagonal];

    private static ReadOnlySpan<float> GradientZ =>
        [0f, 0f, 1f, -1f, Diagonal, Diagonal, -Diagonal, -Diagonal];

    private const float Gain = 0.5f;
    private const float Lacunarity = 2f;

    // A 32-bit avalanche of the cell coordinates and the seed. Mixing is unchecked integer work
    // only, so it is exact everywhere.
    public static uint Hash(int x, int z, ulong seed)
    {
        unchecked
        {
            uint h = (uint)seed ^ ((uint)(seed >> 32) * 0x9E3779B9u);
            h ^= (uint)x * 0x27D4EB2Du;
            h = (h << 13) | (h >> 19);
            h *= 0x165667B1u;
            h ^= (uint)z * 0x85EBCA6Bu;
            h ^= h >> 15;
            h *= 0xC2B2AE35u;
            h ^= h >> 13;
            return h;
        }
    }

    // 0..1 from the top 24 bits, which are the well-mixed ones.
    public static float Hash01(int x, int z, ulong seed)
    {
        return (Hash(x, z, seed) >> 8) * (1f / 16777216f);
    }

    // Gradient (Perlin-style) noise, about -0.71..0.71 for unit gradients.
    public static float Gradient(float x, float z, ulong seed)
    {
        int xi = FloorToInt(x);
        int zi = FloorToInt(z);
        float xf = x - xi;
        float zf = z - zi;
        float u = Fade(xf);
        float v = Fade(zf);

        float n00 = Corner(xi, zi, xf, zf, seed);
        float n10 = Corner(xi + 1, zi, xf - 1f, zf, seed);
        float n01 = Corner(xi, zi + 1, xf, zf - 1f, seed);
        float n11 = Corner(xi + 1, zi + 1, xf - 1f, zf - 1f, seed);

        return Lerp(Lerp(n00, n10, u), Lerp(n01, n11, u), v);
    }

    // Fractal Brownian motion: octaves of Gradient at doubling frequency and halving amplitude,
    // normalised to about -1..1. Pass coordinates already scaled by the base frequency.
    public static float Fbm(float x, float z, ulong seed, int octaves)
    {
        // Loud rather than clamped: silently dropping an octave would hand the caller a different
        // world from the one it asked for.
        ArgumentOutOfRangeException.ThrowIfLessThan(octaves, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(octaves, 8);

        float value = 0f;
        float amplitude = 0.5f;
        float frequency = 1f;
        float norm = 0f;
        for (int octave = 0; octave < octaves; octave++)
        {
            value += amplitude * Gradient(x * frequency, z * frequency, seed + (ulong)octave);
            norm += amplitude;
            amplitude *= Gain;
            frequency *= Lacunarity;
        }

        // Unit gradients peak near 0.71, so the normalised sum rarely reaches +-1; the clamp only
        // guards the tail.
        return Math.Clamp(value / (norm * Diagonal), -1f, 1f);
    }

    // Fbm remapped to 0..1, which is what the material ramps take.
    public static float FbmUnit(float x, float z, ulong seed, int octaves)
    {
        return (Fbm(x, z, seed, octaves) * 0.5f) + 0.5f;
    }

    // Worley (cellular) noise: the distances to the nearest and second-nearest feature point, one
    // point per integer cell of the scaled space, searched over the 3x3 window around the sample.
    // That window is the usual approximation rather than the exact answer -- a point two cells
    // away can beat the f2 computed here -- which only moves where a fracture seam falls, and is
    // as deterministic as the exact version would be. f1 alone gives round bodies; f2 - f1 is near zero
    // exactly on the boundary between two cells, which is the fracture line between two stones.
    // Distances are compared squared and rooted once each at the end.
    public static void Worley(float x, float z, ulong seed, out float f1, out float f2)
    {
        int xi = FloorToInt(x);
        int zi = FloorToInt(z);
        float nearest = float.MaxValue;
        float second = float.MaxValue;

        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int cellX = xi + dx;
                int cellZ = zi + dz;
                float pointX = cellX + Hash01(cellX, cellZ, seed);
                float pointZ = cellZ + Hash01(cellX, cellZ, seed + 0x9E3779B97F4A7C15UL);
                float deltaX = pointX - x;
                float deltaZ = pointZ - z;
                float distance = (deltaX * deltaX) + (deltaZ * deltaZ);
                if (distance < nearest)
                {
                    second = nearest;
                    nearest = distance;
                }
                else if (distance < second)
                {
                    second = distance;
                }
            }
        }

        f1 = MathF.Sqrt(nearest);
        f2 = MathF.Sqrt(second);
    }

    // Widens a 0..1 field about its midpoint. Normalised fBm clusters near 0.5 -- a sum of unit
    // gradients rarely approaches its own bound -- so a ramp placed on the raw field catches almost
    // nothing at the tails and a world ends up all one material. A gain near 3 spreads the middle
    // half of the field across most of 0..1 and saturates the last few percent at each end. Pure
    // float arithmetic, so it stays bit-stable across hosts.
    public static float Contrast(float unitValue, float gain)
    {
        return Math.Clamp(((unitValue - 0.5f) * gain) + 0.5f, 0f, 1f);
    }

    // The usual smoothstep, and it accepts edge0 > edge1 so a caller can write a falling ramp
    // without a separate helper. A zero-width ramp has no direction left to honour, so it steps
    // up at the edge whichever way the caller meant it.
    public static float Smoothstep(float edge0, float edge1, float value)
    {
        if (edge0 == edge1)
        {
            return value >= edge1 ? 1f : 0f;
        }

        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private static float Corner(int cellX, int cellZ, float deltaX, float deltaZ, ulong seed)
    {
        int gradient = (int)(Hash(cellX, cellZ, seed) & 7u);
        return (GradientX[gradient] * deltaX) + (GradientZ[gradient] * deltaZ);
    }

    // Quintic fade: zero first and second derivatives at both ends, so octaves do not show the
    // lattice.
    private static float Fade(float t)
    {
        return t * t * t * ((t * ((t * 6f) - 15f)) + 10f);
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + ((b - a) * t);
    }

    private static int FloorToInt(float value)
    {
        return (int)MathF.Floor(value);
    }
}
