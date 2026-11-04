using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.Simulation.World;

// The noise primitives world generation is built on. They feed Map.WorldChecksum, which hosts
// compare, so the properties that matter are determinism and range, not visual quality.
public class TerrainNoiseTests
{
    private const ulong Seed = 0xFEED_FACE_C0FFEE11UL;

    [Fact]
    public void HashIsStableAndSeedSeparated()
    {
        Assert.Equal(TerrainNoise.Hash(7, -13, Seed), TerrainNoise.Hash(7, -13, Seed));
        Assert.NotEqual(TerrainNoise.Hash(7, -13, Seed), TerrainNoise.Hash(-13, 7, Seed));
        Assert.NotEqual(TerrainNoise.Hash(7, -13, Seed), TerrainNoise.Hash(7, -13, Seed + 1));
    }

    [Fact]
    public void Hash01StaysInTheUnitRange()
    {
        for (int x = -40; x < 40; x++)
        {
            for (int z = -40; z < 40; z++)
            {
                float value = TerrainNoise.Hash01(x, z, Seed);
                Assert.InRange(value, 0f, 1f);
            }
        }
    }

    [Fact]
    public void Hash01SpreadsAcrossTheRange()
    {
        // A hash that clumps would make the per-cell dither useless. Eight buckets over 6400 cells
        // should each hold a tenth of the samples at the very least.
        int[] buckets = new int[8];
        for (int x = 0; x < 80; x++)
        {
            for (int z = 0; z < 80; z++)
            {
                int bucket = Math.Min(7, (int)(TerrainNoise.Hash01(x, z, Seed) * 8f));
                buckets[bucket]++;
            }
        }

        foreach (int bucket in buckets)
        {
            Assert.True(bucket > 6400 / 8 / 2, $"a hash bucket held only {bucket} of 6400 samples");
        }
    }

    [Fact]
    public void FbmStaysInRangeAndIsDeterministic()
    {
        for (int x = -60; x < 60; x++)
        {
            for (int z = -60; z < 60; z++)
            {
                float a = TerrainNoise.Fbm(x * 0.037f, z * 0.037f, Seed, 4);
                float b = TerrainNoise.Fbm(x * 0.037f, z * 0.037f, Seed, 4);
                Assert.Equal(a, b);
                Assert.InRange(a, -1f, 1f);
                Assert.InRange(TerrainNoise.FbmUnit(x * 0.037f, z * 0.037f, Seed, 4), 0f, 1f);
            }
        }
    }

    [Fact]
    public void FbmIsContinuous()
    {
        // Neighbouring samples must not jump: that continuity is the whole difference from the
        // modulo-hash scatter. A tenth of a cell apart, the field may not move by more than a
        // fifth of its range.
        float previous = TerrainNoise.Fbm(0f, 0.5f, Seed, 4);
        for (int step = 1; step <= 400; step++)
        {
            float value = TerrainNoise.Fbm(step * 0.1f * 0.025f, 0.5f, Seed, 4);
            Assert.True(
                MathF.Abs(value - previous) < 0.2f,
                $"the field jumped by {MathF.Abs(value - previous)} at step {step}");
            previous = value;
        }
    }

    [Fact]
    public void FbmUsesEveryOctaveItIsGiven()
    {
        Assert.NotEqual(
            TerrainNoise.Fbm(3.25f, 1.75f, Seed, 1),
            TerrainNoise.Fbm(3.25f, 1.75f, Seed, 4));
    }

    [Fact]
    public void WorleyOrdersItsTwoDistances()
    {
        for (int x = 0; x < 50; x++)
        {
            for (int z = 0; z < 50; z++)
            {
                TerrainNoise.Worley(x * 0.11f, z * 0.11f, Seed, out float f1, out float f2);
                Assert.True(f1 <= f2, $"f1 {f1} exceeded f2 {f2}");
                Assert.InRange(f1, 0f, 2f);
                Assert.InRange(f2, 0f, 3f);
            }
        }
    }

    [Fact]
    public void WorleyFractureLinesExist()
    {
        // f2 - f1 near zero is the boundary between two cells. If no sample is ever near it, the
        // fracture term in the material policy can never fire.
        bool anyBoundary = false;
        bool anyInterior = false;
        for (int x = 0; x < 200 && !(anyBoundary && anyInterior); x++)
        {
            for (int z = 0; z < 200; z++)
            {
                TerrainNoise.Worley(x * 0.11f, z * 0.11f, Seed, out float f1, out float f2);
                anyBoundary |= f2 - f1 < 0.05f;
                anyInterior |= f2 - f1 > 0.3f;
            }
        }

        Assert.True(anyBoundary, "no sample landed on a cell boundary");
        Assert.True(anyInterior, "no sample landed inside a cell");
    }

    [Theory]
    [InlineData(0.2f, 0.8f, 0.1f, 0f)]
    [InlineData(0.2f, 0.8f, 0.9f, 1f)]
    [InlineData(0.2f, 0.8f, 0.5f, 0.5f)]
    // A falling ramp: edge0 above edge1 is allowed, so a policy needs no second helper.
    [InlineData(0.8f, 0.2f, 0.9f, 0f)]
    [InlineData(0.8f, 0.2f, 0.1f, 1f)]
    public void SmoothstepRampsBothWays(float edge0, float edge1, float value, float expected)
    {
        Assert.Equal(expected, TerrainNoise.Smoothstep(edge0, edge1, value), 3);
    }

    [Fact]
    public void SmoothstepHandlesAZeroWidthRamp()
    {
        Assert.Equal(1f, TerrainNoise.Smoothstep(0.5f, 0.5f, 0.5f));
        Assert.Equal(0f, TerrainNoise.Smoothstep(0.5f, 0.5f, 0.49f));
    }
}
