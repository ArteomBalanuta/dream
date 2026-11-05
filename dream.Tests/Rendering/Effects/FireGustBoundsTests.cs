using System.Reflection;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Rendering.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// "Gusts only reduce" is what keeps 1 + A the peak intensity, and the halo's byte ceiling is derived from
// that peak. Sample() ends in a clamp to [1 - A, 1 + A], so a gust that wrongly RAISED the intensity
// would be hidden by the clamp in every test that only looks at Sample's output. These tests look at the
// gust itself (private, reached by reflection) and at the lights the engine actually emits.
public class FireGustBoundsTests
{
    private static readonly MethodInfo GustMethod =
        typeof(FireFlicker).GetMethod("Gust", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("FireFlicker.Gust was renamed or removed; update this test to the new gust function.");

    private static float Gust(double t, int seed) => (float)GustMethod.Invoke(null, [t, seed])!;

    [Fact]
    public void The_Gust_Is_A_Reduction_Factor_In_Zero_To_One_And_GustDepth_Cannot_Invert_The_Signal()
    {
        Assert.InRange(FireFlicker.GustDepth, 0.01f, 0.99f);

        int[] seeds = [0, 1, 7, 12345, -5, int.MaxValue, int.MinValue];
        foreach (int seed in seeds)
        {
            int active = 0;
            int total = 0;
            float peak = 0f;
            for (double t = 0.0; t < 3000.0; t += 0.0137)
            {
                float g = Gust(t, seed);
                Assert.True(g >= 0f && g <= 1f, $"gust {g} at t={t} seed={seed}");
                total++;
                if (g > 0f)
                {
                    active++;
                }

                peak = MathF.Max(peak, g);
            }

            // GustChance 0.30 of windows, each 0.25 s of 1.3 s with a raised-cosine (mean 0.5): the
            // gust is on ~6 % of the time. A dead or always-on gust is a bug in the other direction.
            double fraction = active / (double)total;
            Assert.InRange(fraction, 0.02, 0.12);
            Assert.True(peak > 0.95f, $"seed {seed}: gust never reached its peak ({peak})");
        }
    }

    [Fact]
    public void The_Gust_Is_Finite_And_Bounded_For_Extreme_Clocks()
    {
        foreach (double t in new[] { 0.0, -0.001, -1.3, -1e6, 1e6, 4096.0, 1e12 })
        {
            float g = Gust(t, 3);
            Assert.True(g >= 0f && g <= 1f, $"gust {g} at t={t}");
        }
    }

    [Theory]
    [InlineData(FireFlickerKind.HeldTorch)]
    [InlineData(FireFlickerKind.Brazier)]
    [InlineData(FireFlickerKind.Lamp)]
    public void Intensity_Never_Leaves_One_Plus_Or_Minus_Amplitude_Over_Seeds_Positions_And_A_Long_Sweep(FireFlickerKind kind)
    {
        float amplitude = FireFlicker.Amplitude(kind);
        float max = float.MinValue;
        float min = float.MaxValue;
        Random random = new(99);
        for (int s = 0; s < 40; s++)
        {
            Vector3 position = new((float)random.NextDouble() * 500f - 250f, (float)random.NextDouble() * 30f, (float)random.NextDouble() * 500f - 250f);
            int seed = kind == FireFlickerKind.HeldTorch ? random.Next(1, int.MaxValue) : 0;
            for (double t = 0.0; t < 4100.0; t += 0.0313)
            {
                FireFlickerSample sample = FireFlicker.Sample(kind, position, t, seed);
                Assert.True(float.IsFinite(sample.Intensity));
                max = MathF.Max(max, sample.Intensity);
                min = MathF.Min(min, sample.Intensity);
                float spread = FireFlicker.RadiusFollow * amplitude;
                Assert.InRange(sample.RadiusScale, 1f - spread - 1e-5f, 1f + spread + 1e-5f);
            }
        }

        Assert.True(max <= 1f + amplitude, $"{kind}: peak {max} exceeds 1 + A");
        Assert.True(min >= 1f - amplitude, $"{kind}: floor {min} below 1 - A");
        Assert.True(max > 1f + (0.8f * amplitude), $"{kind}: never came near the peak ({max}), the test is not exercising the range");
    }

    [Fact]
    public void The_Emitted_Lights_Carry_An_Intensity_Within_The_Ceiling_The_Halo_Byte_Is_Derived_From()
    {
        Random random = new(5);
        float torchPeak = 0f;
        float lampPeak = 0f;
        for (int n = 0; n < 20000; n++)
        {
            Vector3 position = new((float)random.NextDouble() * 300f - 150f, (float)random.NextDouble() * 20f, (float)random.NextDouble() * 300f - 150f);
            double clock = random.NextDouble() * 4096.0;

            Assert.True(HeldLightCatalog.TryCreate(Dream.Simulation.Inventory.HeldLightKind.Torch, position, clock, random.Next(1, int.MaxValue), out PointLight torch));
            torchPeak = MathF.Max(torchPeak, torch.Color.X / HeldLightCatalog.TorchColor.X);

            PointLight lamp = LampLightIndex.CreateLight(position, clock);
            lampPeak = MathF.Max(lampPeak, lamp.Color.X / LampLightIndex.LampColor.X);

            FireFlickerSample brazier = FireFlicker.Sample(FireFlickerKind.Brazier, position, clock, 0);
            lampPeak = MathF.Max(lampPeak, brazier.Intensity);
        }

        Assert.True(torchPeak <= 1f + FireFlicker.TorchAmplitude + 1e-5f, $"torch light peak {torchPeak}");
        Assert.True(lampPeak <= 1f + FireFlicker.LampAmplitude + 1e-5f, $"lamp/brazier light peak {lampPeak}");
        // The byte ceilings the catalog derives are only valid under those peaks.
        Assert.True(HeldLightCatalog.TorchColor.X * FxSimulation.HeldHaloTint.X * torchPeak <= 1f);
        Assert.True(PlacedLightIndex.BrazierColor.X * FxSimulation.LampHaloTint.X * lampPeak <= 1f);
    }
}
