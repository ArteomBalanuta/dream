using Dream.Simulation.Determinism;
using Xunit;

namespace Dream.Tests.Simulation.Determinism;

public class DeterministicRandomTests
{
    [Fact]
    public void NextUInt64_MatchesSplitMix64ReferenceOutputs()
    {
        DeterministicRandom fromZero = new(0UL);
        Assert.Equal(0xE220A8397B1DCDAFUL, fromZero.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, fromZero.NextUInt64());
        Assert.Equal(0x06C45D188009454FUL, fromZero.NextUInt64());

        DeterministicRandom fromReferenceSeed = new(1234567UL);
        Assert.Equal(6457827717110365317UL, fromReferenceSeed.NextUInt64());
        Assert.Equal(3203168211198807973UL, fromReferenceSeed.NextUInt64());
        Assert.Equal(9817491932198370423UL, fromReferenceSeed.NextUInt64());
    }

    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        DeterministicRandom first = new(42UL);
        DeterministicRandom second = new(42UL);

        for (int index = 0; index < 64; index++)
        {
            Assert.Equal(first.NextUInt64(), second.NextUInt64());
        }
    }

    [Fact]
    public void NextInt_StaysInBoundsAndIsRoughlyUniform()
    {
        DeterministicRandom random = new(7UL);
        const int minInclusive = -3;
        const int maxExclusive = 4;
        const int samples = 70_000;
        int[] counts = new int[maxExclusive - minInclusive];

        for (int index = 0; index < samples; index++)
        {
            int value = random.NextInt(minInclusive, maxExclusive);
            Assert.InRange(value, minInclusive, maxExclusive - 1);
            counts[value - minInclusive]++;
        }

        int expected = samples / counts.Length;
        foreach (int count in counts)
        {
            Assert.InRange(count, expected * 0.95, expected * 1.05);
        }
    }

    [Fact]
    public void NextInt_HandlesFullIntRangeAndSingleValueRange()
    {
        DeterministicRandom random = new(9UL);

        for (int index = 0; index < 1000; index++)
        {
            Assert.Equal(5, random.NextInt(5, 6));
            int wide = random.NextInt(int.MinValue, int.MaxValue);
            Assert.True(wide < int.MaxValue);
        }
    }

    [Fact]
    public void NextInt_RejectsEmptyRange()
    {
        DeterministicRandom random = new(1UL);

        Assert.Throws<ArgumentOutOfRangeException>(() => random.NextInt(3, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.NextInt(0));
    }

    [Fact]
    public void NextDoubleAndNextSingle_StayInUnitInterval()
    {
        DeterministicRandom random = new(11UL);
        double doubleSum = 0;
        double singleSum = 0;
        const int samples = 20_000;

        for (int index = 0; index < samples; index++)
        {
            double nextDouble = random.NextDouble();
            float nextSingle = random.NextSingle();
            Assert.InRange(nextDouble, 0.0, Math.BitDecrement(1.0));
            Assert.InRange(nextSingle, 0f, MathF.BitDecrement(1f));
            doubleSum += nextDouble;
            singleSum += nextSingle;
        }

        Assert.InRange(doubleSum / samples, 0.49, 0.51);
        Assert.InRange(singleSum / samples, 0.49, 0.51);
    }

    [Fact]
    public void Seed_CombineIsStableAndOrderSensitive()
    {
        Assert.Equal(Seed.Combine(1UL, 2UL), Seed.Combine(1UL, 2UL));
        Assert.NotEqual(Seed.Combine(1UL, 2UL), Seed.Combine(2UL, 1UL));
        Assert.Equal(Seed.Combine(Seed.Combine(1UL, 2UL), 3UL), Seed.Combine(1UL, 2UL, 3UL));
        Assert.NotEqual(Seed.Combine(1UL, 2UL, 3UL), Seed.Combine(1UL, 2UL, 3UL, 4UL));
    }

    [Fact]
    public void Seed_FromCell_DistinguishesNeighborsAndAxisOrder()
    {
        HashSet<ulong> seeds = [];
        for (int x = -2; x <= 2; x++)
        {
            for (int y = -2; y <= 2; y++)
            {
                for (int z = -2; z <= 2; z++)
                {
                    Assert.True(seeds.Add(Seed.FromCell(x, y, z)), $"Seed collision at ({x},{y},{z}).");
                }
            }
        }

        Assert.NotEqual(Seed.FromCell(1, 2, 3), Seed.FromCell(3, 2, 1));
    }

    [Fact]
    public void StableHash_MatchesFnv1a64OverUtf16CodeUnits()
    {
        Assert.Equal(14695981039346656037UL, StableHash.Of(string.Empty));
        Assert.Equal(0xAF63DC4C8601EC8CUL, StableHash.Of("a"));
        Assert.Equal(0x39D85EC922046E5CUL, StableHash.Of("dream"));
        Assert.NotEqual(StableHash.Of("ab"), StableHash.Of("ba"));
    }
}
